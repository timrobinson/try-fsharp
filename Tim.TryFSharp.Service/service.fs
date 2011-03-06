﻿namespace Tim.TryFSharp.Service

open System
open System.Threading
open Tim.TryFSharp.Core

type ServiceState =
    {
        Mailbox : MailboxProcessor<Command>
        CancellationTokenSource : CancellationTokenSource
        RefreshFeedsTimer : Timer
    } with
    static member Create (config : ServiceConfig) =
        let cts = new CancellationTokenSource()

        let mailbox =
            let app : App =
                {
                    OwnServerId = string (Guid.NewGuid())
                    BaseUri = config.BaseUri
                    OwnSessions = Map.empty
                    SlowStop = false
                }

            MailboxProcessor.Start(App.run app, cts.Token)

        let subscribe =
            let rec impl lastSeq =
                async {
                    try
                        let! lastSeq, (results : Change<Message> array) = CouchDB.changes config.BaseUri (Some "app/stdin") lastSeq
                        for result in results do
                            let message =
                                match result.Doc with
                                | Some message -> message
                                | None -> TryFSharpDB.getMessage config.BaseUri result.Id

                            if Option.isNone message.QueueStatus then
                                mailbox.Post (StdIn (result.Id, message))

                        return! impl (Some lastSeq)
                    with ex ->
                        Log.info "%O" ex
                        return! impl None
                }

            impl None

        Async.Start(subscribe, cts.Token)

        let timer =
            fun _ -> mailbox.Post RefreshFeeds
            |> Timer.timer
            |> Timer.every (TimeSpan.FromMinutes(10.0))

        {
            Mailbox = mailbox
            CancellationTokenSource = cts
            RefreshFeedsTimer = timer
        }

    member this.SlowStop () =
        this.Mailbox.Post SlowStop

    interface IDisposable with
        member this.Dispose() =
            ignore (App.shutdown (this.Mailbox.PostAndReply Exit))
            this.RefreshFeedsTimer.Dispose()
            this.CancellationTokenSource.Cancel()
            this.CancellationTokenSource.Dispose()

type Launcher() =
    inherit MarshalByRefObject()

    let mutable state : ServiceState option = None

    override this.InitializeLifetimeService() =
        null

    interface IService with
        member this.Start config =
            state <- Some (ServiceState.Create config)

        member this.SlowStop () =
            match state with
            | Some state -> state.SlowStop()
            | None -> ()

    interface IDisposable with
        member this.Dispose() =
            match state with
            | Some state -> (state :> IDisposable).Dispose()
            | None -> ()

            state <- None
