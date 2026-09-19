using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Uvm.Bridge;

namespace Uvm
{
    // Cooperative receipts are explicitly NOT independent OS observations.
    public static class ObserveActivity
    {
        internal static readonly object Gate=new object();
        internal static readonly Queue<JObject> Events=new Queue<JObject>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Record(string category,string operation,string target)
        {
            try
            {
                if(!ObserveServer.Sharing)return;
                if(!new[]{"file","network","process","lifecycle"}.Contains(category))return;
                var source=Assembly.GetCallingAssembly();
                // Keep credentials, URL paths, query strings and payloads out of receipts.
                if(category=="network")target=Uri.TryCreate(target,UriKind.Absolute,out var uri)?uri.GetComponents(UriComponents.SchemeAndServer,UriFormat.UriEscaped):"(endpoint omitted)";
                // Unity can load a DLL from bytes, leaving Assembly.Location empty.
                var row=new JObject{{"at",DateTimeOffset.UtcNow.ToString("O")},{"assembly_path",source.Location},{"assembly_name",source.FullName},{"assembly_mvid",source.ManifestModule.ModuleVersionId.ToString("D")},{"category",category},{"operation",Limit(operation,160)},{"target",Limit(target,512)},{"provenance","cooperative self-report; not independently confirmed"}};
                lock(Gate){Events.Enqueue(row);while(Events.Count>200)Events.Dequeue();}
            }
            catch{/* Diagnostics must never interrupt the calling mod. */}
        }
        static string Limit(string value,int max)=>(value??"").Length>max?value.Substring(0,max):value??"";
        internal static JArray Snapshot(){lock(Gate)return new JArray(Events.Select(e=>e.DeepClone()));}
    }

    internal sealed class ObserveServer : IDisposable
    {
        readonly CancellationTokenSource stop=new CancellationTokenSource();
        readonly Func<JObject> snapshot;
        readonly BridgeFilters filters;
        readonly object gate=new object();
        NamedPipeServerStream pipe;
        long lastSeen;
        internal static volatile bool Sharing;
        internal string Error="";
        public bool Connected=>DateTime.UtcNow.Ticks-Interlocked.Read(ref lastSeen)<TimeSpan.FromSeconds(12).Ticks;
        public ObserveServer(Func<JObject> snapshot,BridgeFilters filters){this.snapshot=snapshot;this.filters=filters;Sharing=true;Task.Run(Serve);}
        async Task Serve()
        {
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    var acl=new PipeSecurity();acl.SetAccessRuleProtection(true,false);
                    acl.SetOwner(new SecurityIdentifier(ObserveProtocol.UserSid));
                    acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
                    acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ObserveProtocol.UserSid),PipeAccessRights.FullControl,AccessControlType.Allow));
                    using(var stream=new NamedPipeServerStream(ObserveProtocol.PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl))
                    {
                        lock(gate){if(stop.IsCancellationRequested)return;pipe=stream;}
                        await stream.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        using(var timeout=new System.Threading.Timer(_=>{try{stream.Dispose();}catch{}},null,3000,Timeout.Infinite))
                        {
                            if(!ObserveProtocol.GetNamedPipeClientProcessId(stream.SafePipeHandle,out var pid))continue;
                            // A client can close between the kernel PID query and process lookup.
                            try{using(var client=Process.GetProcessById((int)pid))if(!client.ProcessName.Equals("Observe",StringComparison.OrdinalIgnoreCase))continue;}catch(ArgumentException){continue;}
                            var request=JObject.Parse(await ObserveProtocol.Read(stream,2048,stop.Token).ConfigureAwait(false));
                            var nonce=(string)request["nonce"];
                            if((int?)request["schema"]!=2||!Guid.TryParseExact(nonce,"N",out _))continue;
                            filters.Merge((string)request["filters"]);
                            var data=snapshot();data["schema"]=2;data["nonce"]=nonce;data["filters"]=filters.Wire;
                            await ObserveProtocol.Write(stream,data.ToString(Formatting.None),stop.Token).ConfigureAwait(false);
                            var ack=await ObserveProtocol.Read(stream,128,stop.Token).ConfigureAwait(false);
                            if(ack=="ACK "+nonce){Interlocked.Exchange(ref lastSeen,DateTime.UtcNow.Ticks);Error="";}
                        }
                    }
                }
                catch(Exception e){if(stop.IsCancellationRequested)return;if(Error!=e.GetType().Name)Mod.Log.Warn("Observe bridge: "+e);Error=e.GetType().Name;try{await Task.Delay(1000,stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){return;}}
                finally{lock(gate)pipe=null;}
            }
        }
        public void Dispose(){Sharing=false;stop.Cancel();lock(gate){pipe?.Dispose();}Interlocked.Exchange(ref lastSeen,0);lock(ObserveActivity.Gate)ObserveActivity.Events.Clear();}
    }
}
