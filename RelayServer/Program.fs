open System
open System.Net
type Sockets.Socket with
    member this.AsyncConnect endPoint =
        let callback (endPoint, callback, state) =
            this.BeginConnect(endPoint, callback, state)
        Async.FromBeginEnd(endPoint, callback, this.EndConnect)
    member this.AsyncSend(buf: byte [], offset, size, flags: Sockets.SocketFlags) =
        let callback (flags: Sockets.SocketFlags, callback, state) =
            this.BeginSend(buf, offset, size, flags, callback, state)
        Async.FromBeginEnd(flags,callback, this.EndSend)
    member this.AsyncReceive(buf: byte [], offset, size, flags: Sockets.SocketFlags) =
        let callback (flags: Sockets.SocketFlags, callback, state) =
            this.BeginReceive(buf, offset, size, flags, callback, state)
        Async.FromBeginEnd(flags, callback, this.EndReceive)

type state =
    | Off
    | Waiting of byte [] list * (unit -> bool)
    | On of Sockets.Socket        

type message =
    | ConnectTo of IPEndPoint
    | ConnectionEstablished of Sockets.Socket
    | Echo of byte []
    | Stop
    
let connectTo (inbox: MailboxProcessor<_>) endPoint = async {
    let addressFamily = Sockets.AddressFamily.InterNetwork
    let socketType = Sockets.SocketType.Stream
    let protocolType = Sockets.ProtocolType.Tcp
    let socket = new Sockets.Socket(addressFamily, socketType, protocolType)
    System.Threading.Thread.Sleep(2000)
    do! socket.AsyncConnect endPoint
    inbox.Post(ConnectionEstablished socket)
}

let trySend (socket: Sockets.Socket) bytes = async {
    try
        let! length = socket.AsyncSend(bytes, 0, bytes.Length, Sockets.SocketFlags.None)
        assert(length = bytes.Length)
        return true
    with _ ->
        return false
}

let rec apply (inbox: MailboxProcessor<_>) state message = async {
    match state, message with
        | Off, ConnectTo endPoint ->
                connectTo inbox endPoint |> Async.Start
                let timer = System.Diagnostics.Stopwatch.StartNew()
                return Waiting([], fun () -> timer.Elapsed.TotalSeconds > 20.0)
        | Off, ConnectionEstablished socket ->
                return On socket
        | Off, (Echo _ | Stop) | (Waiting _ | On _), Stop -> return Off
        | Waiting(queue, timedout), Echo bytes ->
            return Waiting(bytes::queue, timedout)
        | Waiting(queue, _), ConnectionEstablished socket ->
            let rec loop queue = async {
                match queue with
                | [] -> return On socket
                | bytes::queue ->
                    let! success = trySend socket bytes
                    if success then return! loop queue else return Off
            }
            return! loop (List.rev queue)
        | (Waiting _ | On _), ConnectTo _ -> return state
        | On socket, ConnectionEstablished socket' ->
                    socket'.Close()
                    return On socket
        | On socket, Echo bytes ->
                let! success = trySend socket bytes
                return if success then state else Off
}

let rec wait (inbox: MailboxProcessor<_>) state = async {
    match state with
    | Waiting(_, timedout) ->
        let! message = inbox.TryReceive 1
        match message with
            | None when timedout() ->
               return! wait inbox Off
            | None ->
                return! wait inbox state
            | Some message ->
                let! state = apply inbox state message
                return! wait inbox state
    | Off | On _ ->
        let! message = inbox.Receive()
        let! state = apply inbox state message
        return! wait inbox state
}

let watchdog f x = async {
    while true do
    try
        do! f x
    with exn -> ()
}

let agent = new MailboxProcessor<_>(fun inbox -> watchdog (wait inbox) Off)

do
    agent.Start()
    let addressFamily = Sockets.AddressFamily.InterNetwork
    let socketType = Sockets.SocketType.Stream
    let protocolType = Sockets.ProtocolType.Tcp
    let socket = new Sockets.Socket(addressFamily, socketType, protocolType)
    let address = IPEndPoint(IPAddress.Loopback, 8001)
    socket.Bind address
    socket.Listen 0
    async {
    while true do
    let socket = socket.Accept()
    while true do
    let buffer = [|0uy|]
    let! length = socket.AsyncReceive(buffer, 0, 1, Sockets.SocketFlags.None)
    printf "%c" (char buffer.[0])
    } |> Async.Start
    agent.Post(ConnectTo address)
    // while true do
    for letter in 'A'..'Z' do
        agent.Post(Echo [|byte letter|])
    

// For more information see https://aka.ms/fsharp-console-apps
printfn "Press Enter to exit"
stdin.ReadLine() |> ignore