// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r "paket: groupref build //"
#load "./.fake/build.fsx/intellisense.fsx"

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools
open Fake.Api
open System

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Folder to deposit deploy artifacts
let artifactsDir = __SOURCE_DIRECTORY__ @@ "artifacts"

// Pattern specifying assemblies to be tested
let testProjects = "tests/*.Tests/*.??proj"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "eiriktsarpalis"
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = "TypeShape"

let testFramework = 
    match Environment.environVarOrDefault "testFramework" "" with
    | x when String.IsNullOrWhiteSpace x -> None
    | x -> Some x

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
let release = ReleaseNotes.load "RELEASE_NOTES.md"

// --------------------------------------------------------------------------------------
// Clean build results

Target.create "Clean" (fun _ ->
    Shell.cleanDirs [ "bin" ; artifactsDir ; "temp" ]
)

Target.create "CleanDocs" (fun _ ->
    Shell.cleanDirs ["docs/output"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target.create "Build" ignore

let buildWithConfiguration config =
    DotNet.build(fun c ->
        { c with
            Configuration = DotNet.BuildConfiguration.fromString config

            MSBuildParams =
                { c.MSBuildParams with
                    Properties = [("GenerateAssemblyInfo", "true"); ("Version", release.AssemblyVersion)] }

        }) __SOURCE_DIRECTORY__

Target.create "Build.Emit" (fun _ -> buildWithConfiguration "Release")
Target.create "Build.NoEmit" (fun _ -> buildWithConfiguration "Release-NoEmit")

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner & kill test runner when complete

let runTests config (proj : string) =
    DotNet.test (fun c ->
        { c with
            Configuration = DotNet.BuildConfiguration.fromString config
            NoBuild = true
            // Blame = true
            Framework = testFramework

            MSBuildParams =
                { c.MSBuildParams with
                    Properties = [("ParallelizeAssemblies", "true"); ("ParallelizeTestCollections", "true")] }

            RunSettingsArguments = 
                if Environment.isWindows then None
                else Some " -- RunConfiguration.DisableAppDomain=true" // https://github.com/xunit/xunit/issues/1357
        }) proj

Target.create "RunTests" ignore

Target.create "RunTests.Release" (fun _ ->
    for proj in !! testProjects do
        runTests "Release" proj
)

Target.create "RunTests.Release-NoEmit" (fun _ ->
    for proj in !! testProjects do
        runTests "Release-NoEmit" proj
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.create "NuGet.Bundle" (fun _ ->
    Paket.pack(fun p ->
        { p with
            OutputPath = artifactsDir
            Version = release.NugetVersion
            BuildPlatform = "AnyCpu"
            ReleaseNotes = String.toLines release.Notes })
)

Target.create "NuGet.ValidateSourceLink" (fun _ ->
    do
        let toolPath = __SOURCE_DIRECTORY__ @@ "tools"
        Directory.ensure toolPath
        let p = DotNet.exec id "tool" (sprintf "update --tool-path %s sourcelink" toolPath)
        if not p.OK then failwith "failed to install sourcelink cli tool"
        
        // include tools folder to PATH
        let sep = if Environment.isWindows then ";" else ":"
        let path = Environment.GetEnvironmentVariable("PATH")
        Environment.SetEnvironmentVariable("PATH", toolPath + sep + path)

    for nupkg in !! (artifactsDir @@ "*.nupkg") do
        let p = Shell.Exec("sourcelink", args = sprintf "test %s" nupkg)
        if p <> 0 then failwithf "failed to validate sourcelink for %s" nupkg
)

Target.create "NuGet.Push" (fun _ ->
    Paket.push(fun p ->
        { p with
            WorkingDir = artifactsDir })
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target.create "ReleaseGithub" (fun _ ->
    let remote =
        Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    //StageAll ""
    Git.Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Git.Branches.pushBranch "" remote (Git.Information.getBranchName "")

    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" remote release.NugetVersion

    let client =
        match Environment.GetEnvironmentVariable "OctokitToken" with
        | null -> 
            let user =
                match Environment.environVarOrDefault "github-user" "" with
                | s when not (String.IsNullOrWhiteSpace s) -> s
                | _ -> UserInput.getUserInput "Username: "
            let pw =
                match Environment.environVarOrDefault "github-pw" "" with
                | s when not (String.IsNullOrWhiteSpace s) -> s
                | _ -> UserInput.getUserPassword "Password: "

            GitHub.createClient user pw
        | token -> GitHub.createClientWithToken token

    client
    |> GitHub.draftNewRelease gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> GitHub.publishDraft
    |> Async.RunSynchronously
)

Target.create "BuildPackage" ignore

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target.create "Default" ignore
Target.create "Bundle"  ignore
Target.create "Release" ignore

"Clean"
  ==> "Build.Emit"
  ==> "Build.NoEmit"
  ==> "Build"
  ==> "RunTests.Release"
  ==> "RunTests.Release-NoEmit"
  ==> "RunTests"
  ==> "Default"

"Default"
  ==> "NuGet.Bundle"
  ==> "NuGet.ValidateSourceLink"
  ==> "Bundle"

"Bundle"
  ==> "NuGet.Push"
  ==> "ReleaseGithub"
  ==> "Release"

Target.runOrDefault "Default"