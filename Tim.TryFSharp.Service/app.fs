﻿namespace Tim.TryFSharp.Service

open System
open System.Diagnostics
open System.Net

type App =
    {
        OwnServerId : string
        BaseUri : Uri
        OwnSessions : Map<string, string * FsiProcess>
    }

type Command =
    | Exit of AsyncReplyChannel<App>
    | StdIn of string * Message
    | StdOut of Message
    | Recycle of string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module App =
    let emptySession : Session =
        { 
            Rev = None
            Owner = ""
            Host = Dns.GetHostName()
            ServicePid = int64 (Process.GetCurrentProcess().Id)
            FsiPid = None
        }

    type Claim =
        | OwnSession of FsiProcess
        | OtherSession of string * int64
        | ProcessFailed of Exception

    let claimSession (app : App) (inbox : MailboxProcessor<_>) (sessionName : string) : App * Claim =
        let id = sprintf "session-%s" sessionName

        let rec impl () =
            match TryFSharpDB.getSession app.BaseUri id with
            | Some session ->
                app, OtherSession(session.Host, session.ServicePid)

            | None ->
                let session = { emptySession with Owner = app.OwnServerId }
                match TryFSharpDB.safePutSession app.BaseUri id session with
                | Some rev ->
                    let session = { session with Rev = Some rev.Rev }

                    try
                        let stdOut s =
                            let message : Message =
                                {
                                    Rev = None
                                    Date = Some (DateTime.UtcNow.ToString("o"))
                                    MessageType = "out"
                                    SessionId = sessionName
                                    Message = s
                                    QueueStatus = None
                                }

                            inbox.Post (StdOut message)
    
                        let recycle () =
                            inbox.Post (Recycle id)

                        let proc = new FsiProcess(stdOut, recycle)
                        proc.Start()

                        let session = { session with FsiPid = Some (int64 proc.Process.Id) }
                        let rev = TryFSharpDB.putSession app.BaseUri id session
                        { app with OwnSessions = Map.add id (rev.Rev, proc) app.OwnSessions }, OwnSession proc
                    with ex ->
                        CouchDB.deleteDocument app.BaseUri id rev.Rev
                        app, ProcessFailed ex

                | None ->
                    impl ()

        match Map.tryFind id app.OwnSessions with
        | Some (_, proc) -> app, OwnSession proc
        | None -> impl ()

    let killSession (app : App) (id : string) (rev : string) (proc : FsiProcess) : App =
        try
            CouchDB.deleteDocument app.BaseUri id rev
        with _ -> ()

        (proc :> IDisposable).Dispose()
        { app with OwnSessions = Map.remove id app.OwnSessions }

    let rec run (app : App) (inbox : MailboxProcessor<_>) : Async<unit> =
        let impl =
            function
            | Exit reply ->
                reply.Reply app
                async { return () }

            | StdIn (id, message) ->
                let app, claim = claimSession app inbox message.SessionId
                match claim with
                | OtherSession (host, pid) ->
                    Log.info "Ignoring %s - owned by session %s on %s (pid %d)" id message.SessionId host pid

                | OwnSession proc ->
                    let rev = TryFSharpDB.putMessage app.BaseUri id { message with QueueStatus = Some "done" }
                    let message = { message with Rev = Some rev.Rev }
                    proc.Process.StandardInput.WriteLine message.Message

                | ProcessFailed ex ->
                    Log.info "%O" ex

                run app inbox

            | StdOut message ->
                ignore (TryFSharpDB.postMessage app.BaseUri message)
                run app inbox

            | Recycle id ->
                let app =
                    match Map.tryFind id app.OwnSessions with
                    | Some (rev, proc) -> killSession app id rev proc
                    | None -> app

                run app inbox

        async {
            try
                let! command = inbox.Receive()
                return! impl command
            with ex ->
                Log.info "%O" ex
                return! run app inbox
        }

    let shutdown (app : App) =
        (app, app.OwnSessions)
        ||> Map.fold (fun app id (rev, proc) -> killSession app id rev proc)

