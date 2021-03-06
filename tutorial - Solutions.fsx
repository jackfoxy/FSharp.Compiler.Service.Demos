
//#load "packages/FSharp.Charting.Gtk.0.90.7/FSharp.Charting.Gtk.fsx"


(*
Compiler Services: Project Analysis
==================================

This tutorial demonstrates symbols, projects, interactive compilation/execution and the file system API

*)

#load "ProjectParser.fsx"

//---------------------------------------------------------------------------
// Task 1. Crack an F# project file and get its options

let fsproj = @"C:\GitHub\fsharp\fsharpbinding\FSharp.AutoComplete\FSharp.AutoComplete.fsproj"

let v = ProjectParser.ProjectResolver(fsproj) 

v.Options


//---------------------------------------------------------------------------
// Task 2. Parse and check an entire project


#I "packages/FSharp.Compiler.Service.0.0.62/lib/net45/"
#r "FSharp.Compiler.Service.dll"

open System
open System.Collections.Generic
open Microsoft.FSharp.Compiler.SourceCodeServices

let checker = InteractiveChecker.Create()

let getProjectOptions projFile = 
    checker.GetProjectOptionsFromCommandLineArgs(projFile, ProjectParser.ProjectResolver(projFile).Options)

let projectOptions = getProjectOptions fsproj

let wholeProjectResults = 
    checker.ParseAndCheckProject(projectOptions) 
    |> Async.RunSynchronously


//---------------------------------------------------------------------------
// Task 3. Analyze all uses of all the symbols used in the project to collect some project statistics

module Seq = 
    let frequencyBy f s = 
        s 
        |> Seq.countBy f
        |> Seq.sortBy (snd >> (~-))


let allUsesOfAllSymbols = 
    wholeProjectResults.GetAllUsesOfAllSymbols()
    |> Async.RunSynchronously


// Task 3a. Frequency by display name

allUsesOfAllSymbols |> Seq.frequencyBy (fun su -> su.Symbol.DisplayName) 

// Task 3b. Frequency by kind

allUsesOfAllSymbols |> Seq.frequencyBy (fun su -> su.Symbol :? FSharpMemberFunctionOrValue) 

// Task 3c. Frequency by kind

allUsesOfAllSymbols |> Seq.frequencyBy (fun su -> su.Symbol.GetType().Name) 

// Task 3d. Frequency by kind

allUsesOfAllSymbols |> Seq.frequencyBy (fun su -> 
       match su.Symbol with 
       | :? FSharpMemberFunctionOrValue as mv -> 
           if mv.IsProperty || mv.IsPropertyGetterMethod || mv.IsPropertySetterMethod then 
              "prop"
           elif mv.IsMember then 
              "method"
           elif mv.IsModuleValueOrMember && mv.CurriedParameterGroups.Count = 0 then 
              "value"
           elif mv.IsModuleValueOrMember && mv.IsTypeFunction then  
              "tyfunc"
           elif mv.IsModuleValueOrMember then 
              "function"
           else
              "local"
       | :? FSharpUnionCase as uc -> 
              "unioncase"
       | :? FSharpField as uc -> 
              "field"
       | :? FSharpEntity as e -> 
              if e.IsFSharpModule then  "module"
              elif  e.IsClass || e.IsInterface || e.IsValueType || e.IsDelegate then  "objtype"
              elif  e.IsNamespace then  "namespace"
              else "entity"

       | _ -> "other") 

//---------------------------------------------------------------------------
// Task 4. Look for short variable names in module or member definitions (not locals)


allUsesOfAllSymbols 
    |> Seq.filter (fun su -> 
         su.Symbol.DisplayName.Length <= 2 && 
         match su.Symbol with 
         | :? FSharpMemberFunctionOrValue as v -> v.IsModuleValueOrMember
         | _ -> false )
    |> Seq.frequencyBy (fun su -> su.Symbol.DisplayName)


//---------------------------------------------------------------------------
// Task 5. Generate an efficient "power" function using the dynamic compiler


open Microsoft.FSharp.Compiler.Interactive.Shell
open System
open System.IO
open System.Text

type Evaluator() = 
    // Intialize output and input streams
    let sbOut = new StringBuilder()
    let sbErr = new StringBuilder()
    let inStream = new StringReader("")
    let outStream = new StringWriter(sbOut)
    let errStream = new StringWriter(sbErr)

    // Build command line arguments & start FSI session
    let argv = [| "C:\\fsi.exe" |]
    let allArgs = Array.append argv [|"--noninteractive"; "--optimize+"|]

    let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
    let fsiSession = FsiEvaluationSession.Create(fsiConfig, allArgs, inStream, outStream, errStream)  

    member __.EvalExpression text =
      match fsiSession.EvalExpression(text) with
      | Some value -> value.ReflectionValue
      | None -> failwith "Got no result!"

let evaluator = Evaluator()
let pow0 =
    evaluator.EvalExpression
        """(();   let rec pow n x = if n = 0 then 1.0 else x * pow (n-1) x in pow)"""
      :?> (int -> double -> double)

pow0 10 2.0

let pow1 =
    evaluator.EvalExpression
        """
(();   
 let rec pow n x = let mutable v = 1.0 in for i in 1 .. n do v <- v * x done; v
 pow)"""
      :?> (int -> double -> double)



let pow2 n =
    evaluator.EvalExpression
       ("""
(();   
 let rec pow (x:double) = """ + String.concat " * " (List.replicate n "x") + """
 pow)""")
      :?> (double -> double)


pow2 10 10.0

#time "on"

let pow2_10 = pow2 10 
let pow2_100 = pow2 100 

let powt f = 
    let mutable res = 0.0
    for i in 0 .. 10000000 do 
        res <- f 10.0 
    res

powt (pow0 10)
powt (pow1 10)
powt pow2_10
powt (pow1 100)
powt pow2_100


//---------------------------------------------------------------------------
// Task 6. Create a file system which draws its input from a form


#r "FSharp.Compiler.Service.dll"
open System
open System.IO
open System.Collections.Generic
open System.Text
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library

type MyFileSystem() = 
    let dflt = Shim.FileSystem
    
    let files = Dictionary<_,_>()
    member __.SetFile(file, text:string) = files.[file] <- (DateTime.Now, text)

    interface IFileSystem with
        // Implement the service to open files for reading and writing
        member __.FileStreamReadShim(fileName) = 
            match files.TryGetValue(fileName) with
            | true, (dt,text) -> new MemoryStream(Encoding.UTF8.GetBytes(text)) :> Stream
            | _ -> dflt.FileStreamReadShim(fileName)

        member __.ReadAllBytesShim(fileName) = 
            match files.TryGetValue(fileName) with
            | true, (dt,text) -> Encoding.UTF8.GetBytes(text)
            | _ -> dflt.ReadAllBytesShim(fileName)

        member __.SafeExists(fileName) = files.ContainsKey(fileName) || dflt.SafeExists(fileName)
        member __.GetLastWriteTimeShim(fileName) = 
            match files.TryGetValue(fileName) with
            | true, (dt, text) -> dt
            | _ -> dflt.GetLastWriteTimeShim(fileName)

        member __.FileStreamCreateShim(fileName) = dflt.FileStreamCreateShim(fileName)
        member __.FileStreamWriteExistingShim(fileName) = dflt.FileStreamWriteExistingShim(fileName)
        member __.GetTempPathShim() = dflt.GetTempPathShim()
        member __.GetFullPathShim(fileName) = dflt.GetFullPathShim(fileName)
        member __.IsInvalidPathShim(fileName) = dflt.IsInvalidPathShim(fileName)
        member __.IsPathRootedShim(fileName) = dflt.IsPathRootedShim(fileName)
        member __.FileDelete(fileName) = dflt.FileDelete(fileName)
        member __.AssemblyLoadFrom(fileName) = dflt.AssemblyLoadFrom fileName
        member __.AssemblyLoad(assemblyName) = dflt.AssemblyLoad assemblyName 

let myFileSystem = MyFileSystem()
Shim.FileSystem <- myFileSystem

let fileName1 = @"c:\mycode\test1.fs" // note, the path doesn't exist
let fileName2 = @"c:\mycode\test2.fs" // note, the path doesn't exist

myFileSystem.SetFile(fileName1, """module N
let x = 1""")
myFileSystem.SetFile(fileName2, """module M
let x = N.x + 1""")
FileSystem.ReadAllBytesShim fileName1
FileSystem.ReadAllBytesShim fileName2

//---------------------------------------------------------------------------
// Task 6b. Check with respect to the file system

let projectOptions2 = 
    { projectOptions with 
        ProjectOptions = [| yield! projectOptions.ProjectOptions |> Array.filter(fun s -> not (s.EndsWith ".fs"))
                            yield fileName1;
                            yield fileName2 |] }

let wholeProjectResults2 = 
    checker.ParseAndCheckProject(projectOptions2) 
    |> Async.RunSynchronously

wholeProjectResults2.Errors

//---------------------------------------------------------------------------
// Task 7. Create an IDE

open System.Windows.Forms

for fileName in [fileName1; fileName2] do 
  let tb1 = new TextBox(Dock=DockStyle.Fill, Multiline=true)
  let f1 = new Form(Visible=true, Text=fileName)
  f1.Controls.Add(tb1)
  tb1.TextChanged.Add(fun _ -> printfn "setting..."; myFileSystem.SetFile(fileName, tb1.Text))


async { for i in 0 .. 100 do 
          try 
            do! Async.Sleep 1000
            printfn "checking..."
            let! wholeProjectResults = checker.ParseAndCheckProject(projectOptions2) 
            printfn "checked..."
            for e in wholeProjectResults.Errors do 
               printfn "error: %s" e.Message 
          with e -> 
              printfn "whoiops...: %A" e.Message }
   |> Async.StartImmediate

// Async.CancelDefaultToken()

