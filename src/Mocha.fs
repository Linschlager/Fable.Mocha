namespace Fable.Mocha

open System
open Fable.Core.Testing
open Fable.Core

type FocusState =
    | Normal
    | Pending
    | Focused

type TestCase =
    | SyncTest of string * (unit -> unit) * FocusState
    | AsyncTest of string * (unit -> Async<unit>) * FocusState
    | TestList of string * TestCase list

[<AutoOpen>]
module Test =
    let testCase name body = SyncTest(name, body, Normal)
    let ptestCase name body = SyncTest(name, body, Pending)
    let ftestCase name body = SyncTest(name, body, Focused)
    let testCaseAsync name body = AsyncTest(name, body, Normal)
    let ptestCaseAsync name body = AsyncTest(name, body, Pending)
    let ftestCaseAsync name body = AsyncTest(name, body, Focused)
    let testList name tests = TestList(name, tests)

module private Env =
    [<Emit("new Function(\"try {return this===window;}catch(e){ return false;}\")")>]
    let internal isBrowser : unit -> bool = jsNative
    let insideBrowser = isBrowser()
    [<Emit("typeof WorkerGlobalScope !== 'undefined' && self instanceof WorkerGlobalScope")>]
    let internal insideWorker :  bool = jsNative

[<RequireQualifiedAccess>]
module Expect =
    let areEqual expected actual : unit =
        Assert.AreEqual(expected, actual)

    let notEqual expected actual : unit =
        Assert.NotEqual(expected, actual)

    let areEqualWithMsg expected actual msg : unit =
        Assert.AreEqual(expected, actual, msg)

    let notEqualWithMsg expected actual msg : unit =
        Assert.NotEqual(expected, actual, msg)

    let isTrue cond = areEqual cond true
    let isFalse cond = areEqual cond false
    let isZero number = areEqual 0 number
    let isEmpty (x: 'a seq) = areEqual true (Seq.isEmpty x)
    let pass() = areEqual true true



module private Html =
    type Node = {
        Tag: string;
        Attributes: (string * string) list;
        Content: string
        Children: Node list
    }

    type IDocElement = interface end

    [<Emit("document.createElement($0)")>]
    let createElement (tag: string) : IDocElement = jsNative
    [<Emit("$2.setAttribute($0, $1)")>]
    let setAttr (name: string) (value: string) (el: IDocElement) : unit = jsNative
    [<Emit("$0.appendChild($1)")>]
    let appendChild (parent: IDocElement) (child: IDocElement) : unit = jsNative
    [<Emit("document.getElementById($0)")>]
    let findElement (id: string) : IDocElement = jsNative
    [<Emit("document.body")>]
    let body : IDocElement = jsNative
    [<Emit("$1.innerHTML = $0")>]
    let setInnerHtml (html: string) (el: IDocElement) : unit = jsNative
    let rec createNode (node: Node) =
        let el = createElement node.Tag
        setInnerHtml node.Content el
        for (attrName, attrValue) in node.Attributes do
            setAttr attrName attrValue el
        for child in node.Children do
            let childElement = createNode child
            appendChild el childElement
        el

    let simpleDiv attrs content = { Tag = "div"; Attributes = attrs; Content = content; Children = [] }
    let div attrs children = { Tag = "div"; Attributes = attrs; Content = ""; Children = children }

module Mocha =
    let [<Global>] private describe (name: string) (f: unit->unit) = jsNative
    let [<Global>] private it (msg: string) (f: unit->unit) = jsNative
    let [<Emit("it.skip($0, $1)")>] private itSkip (msg: string) (f: unit->unit) = jsNative
    let [<Emit("it.only($0, $1)")>] private itOnly (msg: string) (f: unit->unit) = jsNative
    let [<Emit("it($0, $1)")>] private itAsync msg (f: (unit -> unit) -> unit) = jsNative
    let [<Emit("it.skip($0, $1)")>] private itSkipAsync msg (f: (unit -> unit) -> unit) = jsNative
    let [<Emit("it.only($0, $1)")>] private itOnlyAsync msg (f: (unit -> unit) -> unit) = jsNative

    let rec isFocused (test: TestCase ) =
        match test with
        | SyncTest(_,_,Focused) -> true
        | AsyncTest(_,_,Focused) -> true
        | TestList(_,tests) ->
            List.exists isFocused tests
        | _ -> false

    let containsFocused (tests: TestCase list) =
        List.exists isFocused tests

    let private runSyncTestInBrowser name test padding =
        try
            test()
            Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:green" padding) ] (sprintf "✔ %s" name)
        with
        | ex ->
            let error : Html.Node = { Tag = "pre"; Attributes = [ "style", "font-size:16px;color:red;margin:10px; padding:10px; border: 1px solid red; border-radius: 10px" ]; Content = ex.Message; Children = [] }
            Html.div [ ] [
                Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:red" padding) ] (sprintf "✘ %s" name)
                error
            ]
    let private runAsyncTestInBrowser name test padding =
        let id = Guid.NewGuid().ToString()
        async {
            do! Async.Sleep 1000
            match! Async.Catch(test()) with
            | Choice1Of2 () ->
                let div = Html.findElement id
                Html.setInnerHtml (sprintf "✔ %s" name) div
                Html.setAttr "style" (sprintf "font-size:16px; padding-left:%dpx;color:green" padding) div
            | Choice2Of2 err ->
                let div = Html.findElement id
                Html.setInnerHtml (sprintf "✘ %s" name) div
                let error : Html.Node = { Tag = "pre"; Attributes = [ "style", "margin:10px; padding:10px; border: 1px solid red; border-radius: 10px" ]; Content = err.Message; Children = [] }
                Html.setAttr "style" (sprintf "font-size:16px; padding-left:%dpx;color:red" padding) div
                Html.appendChild div (Html.createNode error)
        } |> Async.StartImmediate
        Html.simpleDiv [ ("id", id); ("style",sprintf "font-size:16px; padding-left:%dpx;color:gray" padding) ] (sprintf "⏳ %s" name)
    let rec private renderBrowserTests (hasFocusedTests : bool) (tests: TestCase list) (padding: int) : Html.Node list =
        tests
        |> List.map(function
            | SyncTest (name, test, focus) ->
                match focus with
                | Normal when hasFocusedTests ->
                    Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:#B8860B" padding) ] (sprintf "🚧 skipping '%s' due to other focused tests" name)
                | Normal ->
                    runSyncTestInBrowser name test padding
                | Pending ->
                    Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:#B8860B" padding) ] (sprintf "🚧 skipping '%s' due to it being marked as pending" name)
                | Focused ->
                    runSyncTestInBrowser name test padding

            | AsyncTest (name, test, focus) ->
                match focus with
                | Normal when hasFocusedTests ->
                    Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:#B8860B" padding) ] (sprintf "🚧 skipping '%s' due to other focused tests" name)
                | Normal ->
                    runAsyncTestInBrowser name test padding
                | Pending ->
                    Html.simpleDiv [ ("style",sprintf "font-size:16px; padding-left:%dpx; color:#B8860B" padding) ] (sprintf "🚧 skipping '%s' due to it being marked as pending" name)
                | Focused ->
                    runAsyncTestInBrowser name test padding
            | TestList (name, testCases) ->
                let tests = Html.div [] (renderBrowserTests hasFocusedTests testCases (padding + 20))
                let header : Html.Node = { Tag = "div"; Attributes = [ ("style", sprintf "font-size:20px; padding:%dpx" padding) ]; Content = name; Children = [] }
                Html.div [ ("style", "margin-bottom:20px;") ] [ header; tests ])

    let private configureAsyncTest test =
        (fun finished ->
            async {
                match! Async.Catch(test()) with
                | Choice1Of2 () -> do finished()
                | Choice2Of2 err -> do finished(unbox err)
            } |> Async.StartImmediate )

    let rec runTests (tests: TestCase list) =
        if Env.insideBrowser || Env.insideWorker then
            let hasFocusedTests = containsFocused tests
            let container = Html.div [ ("style", "padding:20px;") ] (renderBrowserTests hasFocusedTests tests 0)
            let element = Html.createNode container
            Html.appendChild Html.body element
        else
        for testCase in tests do
            match testCase with
            | SyncTest (msg, test, focus) -> describe msg (fun () -> 
                match focus with
                | Normal -> it msg test
                | Pending -> itSkip msg test
                | Focused -> itOnly msg test
                )
            | AsyncTest (msg, test, focus) ->
                match focus with 
                | Normal -> itAsync msg (configureAsyncTest test)
                | Pending -> itSkipAsync msg (configureAsyncTest test)
                | Focused -> itOnlyAsync msg (configureAsyncTest test)
            | TestList (name, testCases) ->
                describe name <| fun () ->
                    testCases
                    |> List.iter (fun x -> runTests [x])