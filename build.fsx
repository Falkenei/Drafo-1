#r "paket:
nuget BlackFox.Fake.BuildTask
nuget Fake.Core.Target
nuget Fake.Core.Process
nuget Fake.Core.ReleaseNotes
nuget Fake.IO.FileSystem
nuget Fake.DotNet.Cli
nuget Fake.DotNet.MSBuild
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget Fake.DotNet.FSFormatting
nuget Fake.DotNet.Fsi
nuget Fake.DotNet.NuGet
nuget Fake.Api.Github
nuget Fake.DotNet.Testing.Expecto 
nuget Fake.Tools.Git //"

#if !FAKE
#load "./.fake/build.fsx/intellisense.fsx"
#r "netstandard" // Temp fix for https://github.com/dotnet/fsharp/issues/5216
#endif

open BlackFox.Fake
open System.IO
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools

[<AutoOpen>]
/// user interaction prompts for critical build tasks where you may want to interrupt when you see wrong inputs.
module MessagePrompts =

    let prompt (msg:string) =
        System.Console.Write(msg)
        System.Console.ReadLine().Trim()
        |> function | "" -> None | s -> Some s
        |> Option.map (fun s -> s.Replace ("\"","\\\""))

    let rec promptYesNo msg =
        match prompt (sprintf "%s [Yn]: " msg) with
        | Some "Y" | Some "y" -> true
        | Some "N" | Some "n" -> false
        | _ -> System.Console.WriteLine("Sorry, invalid answer"); promptYesNo msg

    let releaseMsg = """This will stage all uncommitted changes, push them to the origin and bump the release version to the latest number in the RELEASE_NOTES.md file. 
        Do you want to continue?"""

    let releaseDocsMsg = """This will push the docs to gh-pages. Remember building the docs prior to this. Do you want to continue?"""

/// Executes a dotnet command in the given working directory
let runDotNet cmd workingDir =
    let result =
        DotNet.exec (DotNet.Options.withWorkingDirectory workingDir) cmd ""
    if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir


    /// Metadata about the project
module ProjectInfo = 

    let project = "Drafo"

    let summary = "Data Transformations"

    let configuration = "Release"

    // Git configuration (used for publishing documentation in gh-pages branch)
    // The profile where the project is posted
    let gitOwner = "CSBiology"
    let gitName = "Drafo"

    let gitHome = sprintf "%s/%s" "https://github.com" gitOwner

    let projectRepo = sprintf "%s/%s/%s" "https://github.com" gitOwner gitName

    let website = "/Drafo"

    let pkgDir = "pkg"

    let release = ReleaseNotes.load "RELEASE_NOTES.md"

    let stableVersion = SemVer.parse release.NugetVersion

    let stableVersionTag = (sprintf "%i.%i.%i" stableVersion.Major stableVersion.Minor stableVersion.Patch )

    let mutable prereleaseSuffix = ""

    let mutable prereleaseTag = ""

    let mutable isPrerelease = false

    let testProject = "tests/Drafo.Tests/Drafo.Tests.fsproj"
/// Barebones, minimal build tasks
module BasicTasks = 

    open ProjectInfo

    let setPrereleaseTag = BuildTask.create "SetPrereleaseTag" [] {
        printfn "Please enter pre-release package suffix"
        let suffix = System.Console.ReadLine()
        prereleaseSuffix <- suffix
        prereleaseTag <- (sprintf "%s-%s" release.NugetVersion suffix)
        isPrerelease <- true
    }

    let clean = BuildTask.create "Clean" [] {
        !! "src/**/bin"
        ++ "src/**/obj"
        ++ "pkg"
        ++ "bin"
        |> Shell.cleanDirs 
    }

    let build = BuildTask.create "Build" [clean] {
        !! "src/**/*.*proj"
        |> Seq.iter (DotNet.build id)
    }

    let copyBinaries = BuildTask.create "CopyBinaries" [clean; build] {
        let targets = 
            !! "src/**/*.??proj"
            -- "src/**/*.shproj"
            |>  Seq.map (fun f -> ((Path.getDirectory f) </> "bin" </> configuration, "bin" </> (Path.GetFileNameWithoutExtension f)))
        for i in targets do printfn "%A" i
        targets
        |>  Seq.iter (fun (fromDir, toDir) -> Shell.copyDir toDir fromDir (fun _ -> true))
    }    

/// Test executing build tasks
module TestTasks = 

    open ProjectInfo
    open BasicTasks

    let runTests = BuildTask.create "RunTests" [clean; build; copyBinaries] {
        let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
        Fake.DotNet.DotNet.test(fun testParams ->
            {
                testParams with
                    Logger = Some "console;verbosity=detailed"
            }
        ) testProject
    }

/// Package creation
module PackageTasks = 

    open ProjectInfo

    open BasicTasks
    open TestTasks

    let pack = BuildTask.create "Pack" [clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "creating stable package with version %s OK?" stableVersionTag ) 
            then
                !! "src/**/*.*proj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                    let msBuildParams =
                        {p.MSBuildParams with 
                            Properties = ([
                                "Version",stableVersionTag
                                "PackageReleaseNotes",  (release.Notes |> String.concat "\r\n")
                            ] @ p.MSBuildParams.Properties)
                        }
                    {
                        p with 
                            MSBuildParams = msBuildParams
                            OutputPath = Some pkgDir
                    }
                ))
        else failwith "aborted"
    }

    let packPrerelease = BuildTask.create "PackPrerelease" [setPrereleaseTag; clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "package tag will be %s OK?" prereleaseTag )
            then 
                !! "src/**/*.*proj"
                //-- "src/**/Plotly.NET.Interactive.fsproj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                            let msBuildParams =
                                {p.MSBuildParams with 
                                    Properties = ([
                                        "Version", prereleaseTag
                                        "PackageReleaseNotes",  (release.Notes |> String.toLines )
                                    ] @ p.MSBuildParams.Properties)
                                }
                            {
                                p with 
                                    VersionSuffix = Some prereleaseSuffix
                                    OutputPath = Some pkgDir
                                    MSBuildParams = msBuildParams
                            }
                ))
        else
            failwith "aborted"
    }

/// Build tasks for documentation setup and development
module DocumentationTasks =

    open ProjectInfo

    open BasicTasks

    let initDocPage = BuildTask.create "InitDocsPage" [] {
        printfn "Please enter filename"
        let filename = System.Console.ReadLine()
        
        printfn "Please enter title"
        let title = System.Console.ReadLine()

        let path = "./docs" </> filename

        let lines = """
    (*** hide ***)
    (*** condition: prepare ***)
    #r @"..\packages\Newtonsoft.Json\lib\netstandard2.0\Newtonsoft.Json.dll"
    #r "../bin/Plotly.NET/netstandard2.1/Plotly.NET.dll"
    (*** condition: ipynb ***)
    #if IPYNB
    #r "nuget: Plotly.NET, {{fsdocs-package-version}}"
    #r "nuget: Plotly.NET.Interactive, {{fsdocs-package-version}}"
    #endif // IPYNB
    (**
    # {{TITLE}}
    [![Binder](https://mybinder.org/badge_logo.svg)](https://mybinder.org/v2/gh/plotly/Plotly.NET/gh-pages?filepath={{FILENAME}}.ipynb)
    *)
    """

        if (promptYesNo (sprintf "creating file %s with title %s OK?" path title)) then
            lines
            |> String.replace "{{FILENAME}}" filename
            |> String.replace "{{TITLE}}" title
            |> fun content -> File.WriteAllText (path,content)
        else
            failwith "aborted"
    }

    let buildDocs = BuildTask.create "BuildDocs" [build; copyBinaries] {
        printfn "building docs with stable version %s" stableVersionTag
        runDotNet 
            (sprintf "fsdocs build --eval --clean --noapidocs --properties Configuration=Release --parameters fsdocs-package-version %s" stableVersionTag)
            "./"
    }

    let buildDocsPrerelease = BuildTask.create "BuildDocsPrerelease" [setPrereleaseTag; build; copyBinaries] {
        printfn "building docs with prerelease version %s" prereleaseTag
        runDotNet 
            (sprintf "fsdocs build --eval --clean --noapidocs --properties Configuration=Release --parameters fsdocs-package-version %s" prereleaseTag)
            "./"
    }

    let watchDocs = BuildTask.create "WatchDocs" [build; copyBinaries] {
        printfn "watching docs with stable version %s" stableVersionTag
        runDotNet 
            (sprintf "fsdocs watch --eval --clean --noapidocs --properties Configuration=Release --parameters fsdocs-package-version %s" stableVersionTag)
            "./"
    }

    let watchDocsPrerelease = BuildTask.create "WatchDocsPrerelease" [setPrereleaseTag; build; copyBinaries] {
        printfn "watching docs with prerelease version %s" prereleaseTag
        runDotNet 
            (sprintf "fsdocs watch --eval --clean --noapidocs --properties Configuration=Release --parameters fsdocs-package-version %s" prereleaseTag)
            "./"
    }

open BasicTasks
open TestTasks

BuildTask.runOrDefault copyBinaries    