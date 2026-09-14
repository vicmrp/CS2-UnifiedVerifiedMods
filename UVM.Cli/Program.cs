using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

if (await RegistryCommands.TryRun(args)) return;

if (args.Length == 0)
{
    ShowHelp();
    return;
}

switch (args[0].ToLowerInvariant())
{
    case "release":
        RunRelease(args);
        break;

    case "verify":
        RunVerify(args);
        break;

    case "manifest":
        RunManifest(args);
        break;

    case "compare":
        RunCompare(args);
        break;

    default:
        ShowHelp();
        break;
}

static void RunRelease(string[] args)
{
    string? packageFolder = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--package" && i + 1 < args.Length)
        {
            packageFolder = args[++i];
        }
    }

    if (packageFolder == null)
    {
        Console.Error.WriteLine(
            "Usage: uvm release --package <folder>");
        Environment.ExitCode = 1;
        return;
    }

    packageFolder = Path.GetFullPath(packageFolder);

    if (!Directory.Exists(packageFolder))
    {
        Console.Error.WriteLine(
            $"Package folder does not exist: {packageFolder}");
        Environment.ExitCode = 1;
        return;
    }

    var currentDirectory = Directory.GetCurrentDirectory();

    // ---------------------------------------------------------
    // Find uvm.json
    // ---------------------------------------------------------

    var uvmJsonPath = FindFileUpwards(
        currentDirectory,
        "uvm.json");

    if (uvmJsonPath == null)
    {
        Console.Error.WriteLine(
            "ERROR: Could not find uvm.json.");
        Environment.ExitCode = 1;
        return;
    }

    var projectDirectory =
        Path.GetDirectoryName(uvmJsonPath)!;

    // ---------------------------------------------------------
    // Find Git repository
    // ---------------------------------------------------------

    var gitRoot = FindGitRoot(projectDirectory);

    if (gitRoot == null)
    {
        Console.Error.WriteLine(
            "ERROR: Could not find Git repository.");
        Environment.ExitCode = 1;
        return;
    }

    // ---------------------------------------------------------
    // Require clean working tree
    // ---------------------------------------------------------

    var gitStatus = RunGit(
        gitRoot,
        "status --porcelain");

    if (!string.IsNullOrWhiteSpace(gitStatus))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "ERROR: Git working tree is not clean.");
        Console.Error.WriteLine();

        Console.Error.WriteLine(gitStatus);

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Commit or discard changes before creating a UVM release.");

        Environment.ExitCode = 1;
        return;
    }

    // ---------------------------------------------------------
    // Commit SHA
    // ---------------------------------------------------------

    var commit = RunGit(
        gitRoot,
        "rev-parse HEAD").Trim();

    if (string.IsNullOrWhiteSpace(commit))
    {
        Console.Error.WriteLine(
            "ERROR: Could not determine Git commit.");

        Environment.ExitCode = 1;
        return;
    }

    // ---------------------------------------------------------
    // Read uvm.json
    // ---------------------------------------------------------

    UvmConfiguration? config;

    try
    {
        config = JsonSerializer.Deserialize<UvmConfiguration>(
            File.ReadAllText(uvmJsonPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"ERROR: Could not read uvm.json: {ex.Message}");

        Environment.ExitCode = 1;
        return;
    }

    if (config == null ||
        config.Schema != 1 ||
        string.IsNullOrWhiteSpace(config.Repository) ||
        string.IsNullOrWhiteSpace(config.Project))
    {
        Console.Error.WriteLine(
            "ERROR: uvm.json is invalid.");

        Environment.ExitCode = 1;
        return;
    }

    // ---------------------------------------------------------
    // Locate PublishConfiguration.xml
    // ---------------------------------------------------------

    var projectFile = Path.Combine(
        gitRoot,
        config.Project.Replace(
            "/",
            Path.DirectorySeparatorChar.ToString()));

    if (!File.Exists(projectFile))
    {
        Console.Error.WriteLine(
            $"ERROR: Project file not found: {projectFile}");

        Environment.ExitCode = 1;
        return;
    }

    var csprojDirectory =
        Path.GetDirectoryName(projectFile)!;

    var publishConfigurations =
        Directory.GetFiles(
            csprojDirectory,
            "PublishConfiguration.xml",
            SearchOption.AllDirectories);

    if (publishConfigurations.Length == 0)
    {
        Console.Error.WriteLine(
            "ERROR: PublishConfiguration.xml was not found.");

        Environment.ExitCode = 1;
        return;
    }

    if (publishConfigurations.Length > 1)
    {
        Console.Error.WriteLine(
            "ERROR: Multiple PublishConfiguration.xml files were found.");

        foreach (var file in publishConfigurations)
            Console.Error.WriteLine($"  {file}");

        Environment.ExitCode = 1;
        return;
    }

    var publishConfigurationPath =
        publishConfigurations[0];

    // ---------------------------------------------------------
    // Read PublishConfiguration.xml
    // ---------------------------------------------------------

    XDocument publishXml;

    try
    {
        publishXml = XDocument.Load(
            publishConfigurationPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"ERROR: Could not read PublishConfiguration.xml: {ex.Message}");

        Environment.ExitCode = 1;
        return;
    }

    var publishRoot = publishXml.Root;

    if (publishRoot == null ||
        publishRoot.Name.LocalName != "Publish")
    {
        Console.Error.WriteLine(
            "ERROR: Invalid PublishConfiguration.xml.");

        Environment.ExitCode = 1;
        return;
    }

    string? GetPublishValue(string elementName)
    {
        return publishRoot
            .Elements()
            .FirstOrDefault(
                x => x.Name.LocalName == elementName)?
            .Attribute("Value")?
            .Value?
            .Trim();
    }

    var modId = GetPublishValue("ModId");
    var displayName = GetPublishValue("DisplayName");
    var modVersion = GetPublishValue("ModVersion");
    var gameVersion = GetPublishValue("GameVersion");

    if (string.IsNullOrWhiteSpace(displayName))
    {
        Console.Error.WriteLine(
            "ERROR: PublishConfiguration.xml has no DisplayName.");

        Environment.ExitCode = 1;
        return;
    }

    if (string.IsNullOrWhiteSpace(modVersion))
    {
        Console.Error.WriteLine(
            "ERROR: PublishConfiguration.xml has no ModVersion.");

        Environment.ExitCode = 1;
        return;
    }

    // UVM convention:
    // ModVersion 1.0 -> GitHub tag v1.0
    var githubReleaseTag = $"v{modVersion}";

    // ---------------------------------------------------------
    // Hash package
    // ---------------------------------------------------------

    var files = new SortedDictionary<string, ManifestFile>(
        StringComparer.OrdinalIgnoreCase);

    foreach (var file in Directory.GetFiles(
                 packageFolder,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizePath(
            Path.GetRelativePath(
                packageFolder,
                file));

        if (ShouldIgnore(relative))
            continue;

        files[relative] = new ManifestFile
        {
            Sha256 = CalculateSha256(file)
        };
    }

    // ---------------------------------------------------------
    // Create manifest
    // ---------------------------------------------------------

    var manifest = new UvmReleaseManifest
    {
        Schema = 1,
        Repository = config.Repository,
        Project = config.Project,
        Commit = commit,

        GithubRelease = new GithubReleaseInfo
        {
            Tag = githubReleaseTag
        },

        Paradox = new ParadoxReleaseInfo
        {
            ModId = string.IsNullOrWhiteSpace(modId)
                ? null
                : modId,

            DisplayName = displayName,
            ModVersion = modVersion,
            GameVersion = gameVersion ?? ""
        },

        GeneratedUtc = DateTime.UtcNow,

        Files = files
    };

    var artifactsDirectory =
        Path.Combine(gitRoot, "artifacts");

    Directory.CreateDirectory(
        artifactsDirectory);

    var outputPath = Path.Combine(
        artifactsDirectory,
        "uvm-manifest.json");

    var json = JsonSerializer.Serialize(
        manifest,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        });

    File.WriteAllText(
        outputPath,
        json);

    // ---------------------------------------------------------
    // Output
    // ---------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("UVM Release");
    Console.WriteLine();

    Console.WriteLine(
        $"Repository:       {config.Repository}");

    Console.WriteLine(
        $"Commit:           {commit}");

    Console.WriteLine(
        $"GitHub release:   {githubReleaseTag}");

    Console.WriteLine(
        $"Display name:     {displayName}");

    Console.WriteLine(
        $"Paradox version:  {modVersion}");

    Console.WriteLine(
        $"Game version:     {gameVersion}");

    if (string.IsNullOrWhiteSpace(modId))
    {
        Console.WriteLine(
            "Paradox Mod ID:  PENDING (first publication)");
    }
    else
    {
        Console.WriteLine(
            $"Paradox Mod ID:  {modId}");
    }

    Console.WriteLine(
        $"Files hashed:     {files.Count}");

    Console.WriteLine();
    Console.WriteLine(
        "Git working tree: CLEAN");

    Console.WriteLine();
    Console.WriteLine(
        $"Created: {outputPath}");

    Console.WriteLine();
    Console.WriteLine(
        $"GitHub release must use tag: {githubReleaseTag}");
}

static void RunVerify(string[] args)
{
    string? packageFolder = null;
    string? manifestPath = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--package" && i + 1 < args.Length)
        {
            packageFolder = args[++i];
        }
        else if (args[i] == "--manifest" && i + 1 < args.Length)
        {
            manifestPath = args[++i];
        }
    }

    if (packageFolder == null || manifestPath == null)
    {
        ShowHelp();
        return;
    }

    packageFolder = Path.GetFullPath(packageFolder);
    manifestPath = Path.GetFullPath(manifestPath);

    if (!Directory.Exists(packageFolder))
    {
        Console.Error.WriteLine($"Package folder does not exist: {packageFolder}");
        Environment.ExitCode = 1;
        return;
    }

    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"Manifest does not exist: {manifestPath}");
        Environment.ExitCode = 1;
        return;
    }

    UvmManifest? manifest;

    try
    {
        var json = File.ReadAllText(manifestPath);

        manifest = JsonSerializer.Deserialize<UvmManifest>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: Could not read manifest: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    if (manifest == null || manifest.Schema != 1)
    {
        Console.Error.WriteLine("ERROR: Invalid UVM manifest.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine();
    Console.WriteLine("UVM Verify");
    Console.WriteLine();

    var failures = 0;
    var matches = 0;

    foreach (var entry in manifest.Files)
    {
        var relative = entry.Key;
        var expectedHash = entry.Value.Sha256;

        var installedPath = Path.Combine(
            packageFolder,
            relative.Replace("/", Path.DirectorySeparatorChar.ToString()));

        if (!File.Exists(installedPath))
        {
            Console.WriteLine($"X {relative} - MISSING");
            failures++;
            continue;
        }

        var actualHash = CalculateSha256(installedPath);

        if (string.Equals(
                expectedHash,
                actualHash,
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"OK {relative}");
            matches++;
        }
        else
        {
            Console.WriteLine($"X {relative} - HASH MISMATCH");
            Console.WriteLine($"    Expected: {expectedHash}");
            Console.WriteLine($"    Actual:   {actualHash}");
            failures++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Repository: {manifest.Repository}");
    Console.WriteLine($"Commit:     {manifest.Commit}");
    Console.WriteLine();

    if (failures == 0)
    {
        Console.WriteLine($"RESULT: VERIFIED ({matches} files)");
    }
    else
    {
        Console.WriteLine($"RESULT: VERIFICATION FAILED ({failures} problem(s))");
        Environment.ExitCode = 1;
    }
}


static void RunManifest(string[] args)
{
    string? packageFolder = null;

    if (args.Length == 2 && !args[1].StartsWith("--"))
    {
        packageFolder = args[1];
    }
    else if (args.Length == 3 && args[1] == "--package")
    {
        packageFolder = args[2];
    }

    if (packageFolder == null)
    {
        ShowHelp();
        return;
    }

    packageFolder = Path.GetFullPath(packageFolder);

    if (!Directory.Exists(packageFolder))
    {
        Console.Error.WriteLine($"Package folder does not exist: {packageFolder}");
        Environment.ExitCode = 1;
        return;
    }

    var startDirectory = Directory.GetCurrentDirectory();

    var uvmJsonPath = FindFileUpwards(startDirectory, "uvm.json");

    if (uvmJsonPath == null)
    {
        Console.Error.WriteLine("ERROR: Could not find uvm.json.");
        Console.Error.WriteLine("Run this command from inside the UVM-enabled mod project.");
        Environment.ExitCode = 1;
        return;
    }

    var projectDirectory = Path.GetDirectoryName(uvmJsonPath)!;

    var gitRoot = FindGitRoot(projectDirectory);

    if (gitRoot == null)
    {
        Console.Error.WriteLine("ERROR: Could not find Git repository.");
        Environment.ExitCode = 1;
        return;
    }

    var gitStatus = RunGit(gitRoot, "status --porcelain");

    if (!string.IsNullOrWhiteSpace(gitStatus))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("ERROR: Git working tree is not clean.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(gitStatus);
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Commit or discard your changes before generating a UVM manifest.");

        Environment.ExitCode = 1;
        return;
    }

    var commit = RunGit(gitRoot, "rev-parse HEAD").Trim();

    if (string.IsNullOrWhiteSpace(commit))
    {
        Console.Error.WriteLine("ERROR: Could not determine Git commit.");
        Environment.ExitCode = 1;
        return;
    }

    UvmConfiguration? config;

    try
    {
        var configJson = File.ReadAllText(uvmJsonPath);

        config = JsonSerializer.Deserialize<UvmConfiguration>(
            configJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: Could not read uvm.json: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    if (config == null ||
        config.Schema != 1 ||
        string.IsNullOrWhiteSpace(config.Repository))
    {
        Console.Error.WriteLine("ERROR: uvm.json is invalid.");
        Environment.ExitCode = 1;
        return;
    }

    var files = new SortedDictionary<string, ManifestFile>(
        StringComparer.OrdinalIgnoreCase);

    foreach (var file in Directory.GetFiles(
                 packageFolder,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizePath(
            Path.GetRelativePath(packageFolder, file));

        if (ShouldIgnore(relative))
            continue;

        files[relative] = new ManifestFile
        {
            Sha256 = CalculateSha256(file)
        };
    }

    var manifest = new UvmManifest
    {
        Schema = 1,
        Repository = config.Repository,
        Project = config.Project,
        Commit = commit,
        GeneratedUtc = DateTime.UtcNow,
        Files = files
    };

    var artifactsDirectory = Path.Combine(gitRoot, "artifacts");

    Directory.CreateDirectory(artifactsDirectory);

    var output = Path.Combine(
        artifactsDirectory,
        "uvm-manifest.json");

    var json = JsonSerializer.Serialize(
        manifest,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    File.WriteAllText(output, json);

    Console.WriteLine();
    Console.WriteLine("UVM Manifest");
    Console.WriteLine();
    Console.WriteLine($"Repository:   {config.Repository}");
    Console.WriteLine($"Project:      {config.Project}");
    Console.WriteLine($"Commit:       {commit}");
    Console.WriteLine($"Package:      {packageFolder}");
    Console.WriteLine($"Files hashed: {files.Count}");
    Console.WriteLine();
    Console.WriteLine("Git status:   CLEAN");
    Console.WriteLine();
    Console.WriteLine($"Created: {output}");
}

static void RunCompare(string[] args)
{
    if (args.Length != 3)
    {
        ShowHelp();
        return;
    }

    var sourceFolder = Path.GetFullPath(args[1]);
    var targetFolder = Path.GetFullPath(args[2]);

    if (!Directory.Exists(sourceFolder))
    {
        Console.Error.WriteLine(
            $"Source folder does not exist: {sourceFolder}");
        Environment.ExitCode = 1;
        return;
    }

    if (!Directory.Exists(targetFolder))
    {
        Console.Error.WriteLine(
            $"Target folder does not exist: {targetFolder}");
        Environment.ExitCode = 1;
        return;
    }

    var sourceFiles = GetFiles(sourceFolder);
    var targetFiles = GetFiles(targetFolder);

    var allFiles = new SortedSet<string>(
        sourceFiles.Keys,
        StringComparer.OrdinalIgnoreCase);

    allFiles.UnionWith(targetFiles.Keys);

    var matches = 0;
    var differences = 0;

    Console.WriteLine();
    Console.WriteLine("UVM Compare");
    Console.WriteLine();

    foreach (var relative in allFiles)
    {
        var sourceExists = sourceFiles.TryGetValue(
            relative,
            out var sourcePath);

        var targetExists = targetFiles.TryGetValue(
            relative,
            out var targetPath);

        if (!sourceExists)
        {
            Console.WriteLine($"X {relative} - only exists in target");
            differences++;
            continue;
        }

        if (!targetExists)
        {
            Console.WriteLine($"X {relative} - missing from target");
            differences++;
            continue;
        }

        var sourceHash = CalculateSha256(sourcePath!);
        var targetHash = CalculateSha256(targetPath!);

        if (string.Equals(
                sourceHash,
                targetHash,
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"OK {relative}");
            matches++;
        }
        else
        {
            Console.WriteLine($"X {relative} - DIFFERENT");
            Console.WriteLine($"    Source: {sourceHash}");
            Console.WriteLine($"    Target: {targetHash}");
            differences++;
        }
    }

    Console.WriteLine();

    if (differences == 0)
    {
        Console.WriteLine($"RESULT: IDENTICAL ({matches} files)");
    }
    else
    {
        Console.WriteLine(
            $"RESULT: DIFFERENT ({differences} problem(s))");

        Environment.ExitCode = 1;
    }
}

static Dictionary<string, string> GetFiles(string folder)
{
    var result = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase);

    foreach (var file in Directory.GetFiles(
                 folder,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizePath(
            Path.GetRelativePath(folder, file));

        if (ShouldIgnore(relative))
            continue;

        result[relative] = file;
    }

    return result;
}

static string? FindFileUpwards(
    string startDirectory,
    string fileName)
{
    var directory = new DirectoryInfo(startDirectory);

    while (directory != null)
    {
        var candidate = Path.Combine(
            directory.FullName,
            fileName);

        if (File.Exists(candidate))
            return candidate;

        directory = directory.Parent;
    }

    return null;
}

static string? FindGitRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);

    while (directory != null)
    {
        var gitPath = Path.Combine(
            directory.FullName,
            ".git");

        if (Directory.Exists(gitPath) || File.Exists(gitPath))
            return directory.FullName;

        directory = directory.Parent;
    }

    return null;
}

static string RunGit(
    string workingDirectory,
    string arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(startInfo);

    if (process == null)
        return string.Empty;

    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine(error);
        return string.Empty;
    }

    return output;
}

static string CalculateSha256(string path)
{
    using var sha256 = SHA256.Create();
    using var stream = File.OpenRead(path);

    return Convert
        .ToHexString(sha256.ComputeHash(stream))
        .ToLowerInvariant();
}

static string NormalizePath(string path)
{
    return path.Replace("\\", "/");
}

static bool ShouldIgnore(string path)
{
    return
        path.Equals(
            "uvm-manifest.json",
            StringComparison.OrdinalIgnoreCase)
        ||
        Path.GetExtension(path)
            .Equals(
                ".pdb",
                StringComparison.OrdinalIgnoreCase);
}

static void ShowHelp()
{
    RegistryCommands.Help();
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  uvm manifest <folder>");
    Console.WriteLine("  uvm manifest --package <folder>");
    Console.WriteLine("  uvm compare <source-folder> <target-folder>");
}

public sealed class UvmConfiguration
{
    public int Schema { get; set; }

    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";
}

public sealed class UvmManifest
{
    public int Schema { get; set; }

    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string Commit { get; set; } = "";

    public DateTime GeneratedUtc { get; set; }

    public SortedDictionary<string, ManifestFile> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ManifestFile
{
    public string Sha256 { get; set; } = "";
}
