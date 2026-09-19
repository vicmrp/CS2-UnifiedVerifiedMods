using System;
using System.Collections.Generic;
using System.IO;

namespace Uvm
{
    internal static class ModCodeClassifier
    {
        // Null means inspection was incomplete. Do not silently classify it as assets-only.
        internal static bool? HasCode(string folder)
        {
            var pending=new Stack<string>();pending.Push(folder);int entries=0;
            try
            {
                while(pending.Count>0)
                {
                    var current=pending.Pop();
                    if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return null;
                    foreach(var file in Directory.EnumerateFileSystemEntries(current))
                    {
                        if(++entries>4096)return null;
                        var flags=File.GetAttributes(file);
                        if((flags&FileAttributes.ReparsePoint)!=0)return null;
                        if((flags&FileAttributes.Directory)!=0)
                        {
                            if(!Path.GetFileName(file).StartsWith("."))pending.Push(file);
                        }
                        else if(Scanner.CodeExtensions.Contains(Path.GetExtension(file)))return true;
                    }
                }
                return false;
            }
            catch(IOException){return null;}
            catch(UnauthorizedAccessException){return null;}
        }
    }
}
