using CSharPN.Core;

namespace HierarchicalExamples;

// ── HierarchicalProtocol ──────────────────────────────────────────────────────
//
// Hierarchical CPN of Jensen's Simple Protocol (Vol. 2 §2.1).
//
// Page structure:
//
//   TopPage (HierarchicalCpnModel)
//   ├── Place: A : Packet   (data channel — port shared with both sub-pages)
//   ├── Place: B : int      (ack channel  — port shared with both sub-pages)
//   ├── Substitution: "Sender"   → SenderPage
//   └── Substitution: "Receiver" → ReceiverPage
//
//   SenderPage  (CpnPage)
//   ├── Port In:  B  (acks arrive)
//   ├── Port Out: A  (packets depart)
//   └── Local places: NextToSend
//       Transitions: SendPacket, LosePacket,
//                    ReceiveAck_Correct, ReceiveAck_Dup
//
//   ReceiverPage (CpnPage)
//   ├── Port In:  A  (packets arrive)
//   ├── Port Out: B  (acks depart)
//   └── Local places: NextRec, Received
//       Transitions: ReceivePacket_New, ReceivePacket_Dup, LoseAck
//
// After AddSubPage, the HierarchicalCpnModel's flat transition list contains
// all 7 transitions from both sub-pages; the 2 channel places are shared.

public static class HierarchicalProtocol
{
    // ── Colour set ────────────────────────────────────────────────────────────
    public sealed record Packet(int Seq, string Data);

    private static readonly string[] DataItems =
        ["a", "b", "c", "d", "e", "f", "g", "h"];

    // ── SenderPage ────────────────────────────────────────────────────────────
    private sealed class SenderPage : CpnPage
    {
        public readonly Place<int> NextToSend;

        public SenderPage(Place<Packet> channelA, Place<int> channelB)
            : base("Sender")
        {
            Out(channelA);
            In(channelB);

            NextToSend = AddPlace<int>("NextToSend", Multiset.Of(0));

            var n = new Var<int>("n");
            var k = new Var<int>("k");
            var p = new Var<Packet>("p_lose"); // used only for consumption

            // Transmit (or retransmit) current packet.
            AddTransition("SendPacket")
                .Input(NextToSend, n)
                .Guard(() => n.Val < DataItems.Length)
                .Output(NextToSend, () => n.Val)
                .Output(channelA, () => new Packet(n.Val % 2, DataItems[n.Val]))
                .Build();

            // Non-deterministic packet loss on channel A.
            AddTransition("LosePacket")
                .Input(channelA, p)
                .Build();

            // Correct ack: advance NextToSend.
            AddTransition("ReceiveAck_Correct")
                .Input(NextToSend, n)
                .Input(channelB, k)
                .Guard(() => n.Val < DataItems.Length && k.Val == n.Val + 1)
                .Output(NextToSend, () => n.Val + 1)
                .Build();

            // Stale ack: ignore (keep NextToSend).
            AddTransition("ReceiveAck_Dup")
                .Input(NextToSend, n)
                .Input(channelB, k)
                .Guard(() => k.Val != n.Val + 1)
                .Output(NextToSend, () => n.Val)
                .Build();
        }
    }

    // ── ReceiverPage ──────────────────────────────────────────────────────────
    private sealed class ReceiverPage : CpnPage
    {
        public readonly Place<int>    NextRec;
        public readonly Place<string> Received;

        public ReceiverPage(Place<Packet> channelA, Place<int> channelB)
            : base("Receiver")
        {
            In(channelA);
            Out(channelB);

            NextRec  = AddPlace<int>("NextRec", Multiset.Of(0));
            Received = AddPlace<string>("Received");

            var p = new Var<Packet>("p");
            var k = new Var<int>("k");

            // New packet (correct sequence bit): accept and send ack.
            AddTransition("ReceivePacket_New")
                .Input(channelA, p)
                .Input(NextRec, k)
                .Guard(() => p.Val.Seq == k.Val % 2)
                .Output(NextRec, () => k.Val + 1)
                .Output(Received, () => p.Val.Data)
                .Output(channelB, () => k.Val + 1)
                .Build();

            // Duplicate packet (wrong sequence bit): discard, re-send previous ack.
            AddTransition("ReceivePacket_Dup")
                .Input(channelA, p)
                .Input(NextRec, k)
                .Guard(() => p.Val.Seq != k.Val % 2)
                .Output(NextRec, () => k.Val)
                .Output(channelB, () => k.Val)
                .Build();

            // Non-deterministic ack loss on channel B.
            AddTransition("LoseAck")
                .Input(channelB, k)
                .Build();
        }
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the hierarchical model.  Returns both the model and references
    /// to the receiver page's result places for external inspection.
    /// </summary>
    public static (HierarchicalCpnModel model,
                   Place<string> received,
                   Place<int>    nextToSend) Build()
    {
        var model = new HierarchicalCpnModel("HierarchicalSimpleProtocol");

        // Top-level channel places (shared ports for both sub-pages).
        var channelA = model.AddPlace<Packet>("A", Multiset<Packet>.Empty);
        var channelB = model.AddPlace<int>("B",    Multiset<int>.Empty);

        var sender   = new SenderPage(channelA, channelB);
        var receiver = new ReceiverPage(channelA, channelB);

        model.AddSubPage(sender,   "Sender");
        model.AddSubPage(receiver, "Receiver");

        return (model, receiver.Received, sender.NextToSend);
    }
}
