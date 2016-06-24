/// The MIT License (MIT)
/// Copyright (c) 2016 Bazinga Technologies Inc

namespace FSharp.Data.GraphQL.Client

open System
open System.IO
open System.Net
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices

open FSharp.Data.GraphQL.Types.Introspection
open TypeCompiler
open System.Collections.Generic
open Newtonsoft.Json.Linq
open Newtonsoft.Json

module Util =
    let requestSchema (url: string) =
        async {
            let requestUrl = Uri(Uri(url), ("/?query=" + FSharp.Data.GraphQL.Introspection.introspectionQuery))
            let req = WebRequest.CreateHttp(requestUrl)
            req.Method <- "GET"
            use! resp = req.GetResponseAsync() |> Async.AwaitTask
            use stream = resp.GetResponseStream()
            use reader = new StreamReader(stream)
            let! json = reader.ReadToEndAsync() |> Async.AwaitTask
            let result = Serialization.fromJson json
            match result.Errors with
            | None ->
                let introspectionSchema = result.Data.__schema
                return Choice1Of2 introspectionSchema
            | Some errors ->
                return Choice2Of2 errors
        }

    let compileTypesFromSchema asm ns (schema: IntrospectionSchema) = 
        let ctx = {
            Assembly = asm
            Namespace = ns
            KnownTypes = ProviderSessionContext.CoreTypes }
        let typeDefinitions =
            (ctx.KnownTypes, schema.Types)
            ||> Array.fold (fun acc t ->
                if acc.ContainsKey t.Name
                then acc
                else Map.add t.Name (ProvidedType (initType ctx t, t)) acc) 
        let defctx = { ctx with KnownTypes = typeDefinitions }
        typeDefinitions
        |> Seq.choose (fun kv -> 
            match kv.Value with
            | NativeType _ -> None
            | ProvidedType (t, itype) ->
                genType defctx itype t
                Some t)
        |> Seq.toList

    let rec jsonToObject (token: JToken) =
        match token.Type with
        | JTokenType.Object ->
            token.Children<JProperty>()
            |> Seq.map (fun prop -> prop.Name, jsonToObject prop.Value)
            |> dict :> obj
        | JTokenType.Array ->
            token |> Seq.map jsonToObject |> Seq.toArray :> obj
        | _ ->
            (token :?> JValue).Value

    let launchQuery (serverUrl: string) (query: string) =
        async {
            use client = new WebClient()
            let query = Map["query", query] |> JsonConvert.SerializeObject
            let! json = client.UploadStringTaskAsync(Uri(serverUrl), query) |> Async.AwaitTask
            let result = JToken.Parse json |> jsonToObject :?> IDictionary<string,obj>
            match result.TryGetValue("errors") with
            | false, _ -> return Choice1Of2 result.["data"]
            | true, errors -> return Choice2Of2 errors
        }


type internal ProviderSchemaConfig =
    { Namespace: string 
      DefinedTypes: Map<string, ProvidedTypeDefinition option> }

[<TypeProvider>]
type GraphQlProvider (config : TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces ()
    
    let asm = System.Reflection.Assembly.GetExecutingAssembly()

    do
        let ns = "FSharp.Data.GraphQL"
        let generator = ProvidedTypeDefinition(asm, ns, "GraphQLProvider", Some typeof<obj>)
        generator.DefineStaticParameters([ProvidedStaticParameter("url", typeof<string>)], fun typeName parameterValues ->
            match parameterValues with 
            | [| :? string as url|] ->
                let choice = Util.requestSchema(url) |> Async.RunSynchronously
                match choice with
                | Choice1Of2 schema ->
                    let tdef = ProvidedTypeDefinition(asm, ns, typeName, Some typeof<obj>)
                    let types = Util.compileTypesFromSchema asm "GraphQLTypes" schema
                    tdef.AddMembers(types)
                    let m = ProvidedMethod("Query", [], typeof<Async<Choice<obj,obj>>>, IsStaticMethod=true)
                    m.DefineStaticParameters([ProvidedStaticParameter("query", typeof<string>)], fun methName parameterValues ->
                        match parameterValues with 
                        | [| :? string as query|] ->
                            // Make a first flight to be sure the query is accepted by the server
                            match Util.launchQuery url query |> Async.RunSynchronously with
                            | Choice1Of2 data -> ()
                            | Choice2Of2 errors -> failwithf "%A" errors
                            let m2 = ProvidedMethod(methName, [], typeof<Async<Choice<obj,obj>>>, IsStaticMethod = true) 
                            m2.InvokeCode <- fun _ -> <@@ Util.launchQuery url query @@>
                            tdef.AddMember m2
                            m2
                        | _ -> failwith "unexpected parameter values")
                    m.InvokeCode <- fun _ -> <@@ null @@> // Dummy code
                    tdef.AddMember m
                    tdef
                | Choice2Of2 ex -> String.concat "\n" ex |> failwithf "%s"
            | _ -> failwith "unexpected parameter values")
        this.AddNamespace(ns, [generator])

[<assembly:TypeProviderAssembly>]
do ()