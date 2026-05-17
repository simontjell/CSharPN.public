using CSharPN.Core;

/// <summary>
/// Jensen's canonical Simple Protocol (Coloured Petri Nets, Vol. 1, Ch. 2).
///
/// The sender transmits 8 data items (a..h) using an alternating sequence bit (0/1).
/// Channel A carries data packets from sender to receiver.
/// Channel B carries acknowledgements from receiver to sender.
/// Both channels are unreliable: messages may be lost.
///
/// Places:
///   NextToSend  – packet index being (re)transmitted (0..8)
///   A           – data channel (packets in transit)
///   B           – ack channel (ack bits in transit)
///   Received    – delivered data items (ordered)
///   NextRec     – receiver's expected sequence bit
///
/// Transitions:
///   SendPacket          – sender puts a new packet on channel A
///   LosePacket          – channel A loses a packet (non-deterministic)
///   ReceivePacket_New   – receiver accepts a packet with the expected seq bit
///   ReceivePacket_Dup   – receiver discards a duplicate packet, re-acks
///   LoseAck             – channel B loses an ack (non-deterministic)
///   ReceiveAck_Correct  – sender advances to the next packet
///   ReceiveAck_Dup      – sender receives stale ack and ignores it
/// </summary>
public class AlternatingBitProtocol : CpnModel
{
    public record Packet(int Seq, string Data)
    {
        public override string ToString() => $"({Seq},{Data})";
    }

    // Places
    public readonly Place<int>    NextToSend;
    public readonly Place<Packet> A;
    public readonly Place<int>    B;
    public readonly Place<string> Received;
    public readonly Place<int>    NextRec;

    // Constants
    public static readonly string[] AllData = ["a", "b", "c", "d", "e", "f", "g", "h"];
    public const int TotalPackets = 8;

    /// <summary>True when all 8 packets have been acknowledged and channels are empty.</summary>
    public bool IsComplete =>
        NextToSend.Marking.Count(TotalPackets) >= 1 &&
        A.Marking.IsEmpty &&
        B.Marking.IsEmpty;

    public AlternatingBitProtocol() : base("AlternatingBitProtocol")
    {
        NextToSend = AddPlace("NextToSend", Multiset.Of(0));
        A          = AddPlace<Packet>("A");
        B          = AddPlace<int>("B");
        Received   = AddPlace<string>("Received");
        NextRec    = AddPlace("NextRec", Multiset.Of(0));

        // Variables
        var k   = new Var<int>("k");
        var pkt = new Var<Packet>("pkt");
        var s   = new Var<int>("s");
        var nr  = new Var<int>("nr");

        // 1. SendPacket: sender (re)transmits the current packet onto channel A
        AddTransition("SendPacket")
            .Input(NextToSend, k)
            .Guard(() => k.Val < TotalPackets)
            .Output(NextToSend, k)
            .Output(A, () => new Packet(k.Val % 2, AllData[k.Val]))
            .Build();

        // 2. LosePacket: channel A silently drops a packet
        AddTransition("LosePacket")
            .Input(A, new Var<Packet>("_p"))
            .Build();

        // 3. ReceivePacket_New: receiver gets expected packet, acks, delivers data
        AddTransition("ReceivePacket_New")
            .Input(A, pkt)
            .Input(NextRec, nr)
            .Guard(() => pkt.Val.Seq == nr.Val)
            .Output(NextRec,  () => 1 - nr.Val)
            .Output(B,        () => pkt.Val.Seq)
            .Output(Received, () => pkt.Val.Data)
            .Build();

        // 4. ReceivePacket_Dup: receiver gets duplicate packet, re-acks previous
        AddTransition("ReceivePacket_Dup")
            .Input(A, pkt)
            .Input(NextRec, nr)
            .Guard(() => pkt.Val.Seq != nr.Val)
            .Output(NextRec, nr)
            .Output(B,       () => pkt.Val.Seq)
            .Build();

        // 5. LoseAck: channel B silently drops an ack
        AddTransition("LoseAck")
            .Input(B, new Var<int>("_s"))
            .Build();

        // 6. ReceiveAck_Correct: sender gets expected ack, advances to next packet
        AddTransition("ReceiveAck_Correct")
            .Input(B, s)
            .Input(NextToSend, k)
            .Guard(() => k.Val < TotalPackets && s.Val == k.Val % 2)
            .Output(NextToSend, () => k.Val + 1)
            .Build();

        // 7. ReceiveAck_Dup: sender gets stale ack (wrong bit or already done), ignores
        AddTransition("ReceiveAck_Dup")
            .Input(B, s)
            .Input(NextToSend, k)
            .Guard(() => k.Val >= TotalPackets || s.Val != k.Val % 2)
            .Output(NextToSend, k)
            .Build();
    }
}
