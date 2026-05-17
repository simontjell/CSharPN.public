using CSharPN.Core;

namespace TimedExamples;

// ── NetworkProtocolTimed ──────────────────────────────────────────────────────
//
// Jensen's Simple Protocol with channel propagation delays (Vol. 1 §4.1 / Vol. 2 §1).
//
// Extension over the untimed version:
//   • Channel A (data)  has a propagation delay of DataDelay  time units.
//   • Channel B (ack)   has a propagation delay of AckDelay   time units.
//   • The sender uses a retransmission timer: after sending packet n it waits
//     RetransmitTimeout before being allowed to re-send (models the protocol timer).
//
// Colour sets:
//   Packet  = (Seq: 0|1, Data: string)
//   int     = sequence bit / ack value / packet index
//   string  = delivered data item
//
// Places:
//   NextToSend   : int            – index of current packet (0..DataCount)
//   SendTimer    : Timed<int>     – paces retransmission; value is a dummy int
//   A            : Timed<Packet>  – data channel (packets in transit)
//   B            : Timed<int>     – ack channel  (acks in transit)
//   NextRec      : int            – receiver's next expected packet index
//   Received     : string         – multiset of delivered data items

public sealed class NetworkProtocolTimed : TimedCpnModel
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private static readonly string[] Data =
        ["a", "b", "c", "d", "e", "f", "g", "h"];

    private const int DataDelay          = 7;   // channel A propagation time
    private const int AckDelay           = 5;   // channel B propagation time
    private const int RetransmitTimeout  = 15;  // sender retransmission timer

    // ── Public places (for result inspection) ─────────────────────────────────
    public readonly Place<int>           NextToSend;
    public readonly Place<Timed<int>>    SendTimer;
    public readonly Place<Timed<Packet>> A;
    public readonly Place<Timed<int>>    B;
    public readonly Place<int>           NextRec;
    public readonly Place<string>        Received;

    public sealed record Packet(int Seq, string Data);

    public NetworkProtocolTimed()
    {
        NextToSend = AddPlace<int>("NextToSend", Multiset.Of(0));

        // Timer starts ready at time 0 — sender may send immediately.
        SendTimer = AddTimedPlace<int>("SendTimer",
            Multiset<Timed<int>>.Empty.Add(Timed<int>.At(0, 0), 1));

        A        = AddTimedPlace<Packet>("A");
        B        = AddTimedPlace<int>("B");
        NextRec  = AddPlace<int>("NextRec", Multiset.Of(0));
        Received = AddPlace<string>("Received");

        // ── Variables ─────────────────────────────────────────────────────────
        var n    = new Var<int>("n");
        var tm   = new Var<int>("tm");     // SendTimer value
        var p    = new Var<Packet>("p");
        var k    = new Var<int>("k");

        // ── Transitions ───────────────────────────────────────────────────────

        // SendPacket: consume a ready timer token; transmit current packet with delay.
        AddTransition("SendPacket")
            .Input(NextToSend, n)
            .TimedInput(SendTimer, tm)
            .Guard(() => n.Val < Data.Length)
            .Output(NextToSend, () => n.Val)
            .TimedOutput(A, () => new Packet(n.Val % 2, Data[n.Val]), DataDelay)
            .TimedOutput(SendTimer, () => 0, RetransmitTimeout)   // restart timer
            .Build();

        // LosePacket: packet is lost in channel A (non-deterministic).
        AddTransition("LosePacket")
            .TimedInput(A, p)
            .Build();

        // ReceivePacket_New: correct sequence bit → accept, deliver, send ack.
        AddTransition("ReceivePacket_New")
            .TimedInput(A, p)
            .Input(NextRec, k)
            .Guard(() => p.Val.Seq == k.Val % 2)
            .Output(NextRec, () => k.Val + 1)
            .Output(Received, () => p.Val.Data)
            .TimedOutput(B, () => k.Val + 1, AckDelay)
            .Build();

        // ReceivePacket_Dup: wrong sequence bit → discard, re-send previous ack.
        AddTransition("ReceivePacket_Dup")
            .TimedInput(A, p)
            .Input(NextRec, k)
            .Guard(() => p.Val.Seq != k.Val % 2)
            .Output(NextRec, () => k.Val)
            .TimedOutput(B, () => k.Val, AckDelay)
            .Build();

        // LoseAck: ack is lost in channel B.
        AddTransition("LoseAck")
            .TimedInput(B, k)
            .Build();

        // ReceiveAck_Correct: expected ack received → advance NextToSend.
        AddTransition("ReceiveAck_Correct")
            .Input(NextToSend, n)
            .TimedInput(B, k)
            .Guard(() => n.Val < Data.Length && k.Val == n.Val + 1)
            .Output(NextToSend, () => n.Val + 1)
            .Build();

        // ReceiveAck_Dup: stale ack → keep current NextToSend.
        AddTransition("ReceiveAck_Dup")
            .Input(NextToSend, n)
            .TimedInput(B, k)
            .Guard(() => k.Val != n.Val + 1)
            .Output(NextToSend, () => n.Val)
            .Build();
    }

    // ── Result inspection ──────────────────────────────────────────────────────
    public bool   IsComplete      => NextToSend.Marking.Count(Data.Length) > 0;
    public int    PacketsDelivered => Received.Marking.TotalCount;
    public string ReceivedItems   => string.Join("+", Received.Marking.DistinctItems().Order());
}
