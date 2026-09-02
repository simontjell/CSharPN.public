using CSharPN.Core;

/// <summary>
/// The simple protocol of Jensen &amp; Kristensen, <i>Coloured Petri Nets</i> (2009),
/// Chapter 2 (Fig. 2.1) — the running example of the book's introduction to CPN
/// and of the semantics tests in <c>CSharPN.Core.Tests</c>.
/// </summary>
/// <remarks>
/// <para>
/// Colour sets: NO = <see cref="int"/>, DATA = <see cref="string"/>,
/// NOxDATA = <see cref="Packet"/>, BOOL = <see cref="bool"/>.
/// </para>
/// <para>
/// A sender transmits numbered packets over an unreliable network to a receiver that
/// acknowledges each packet with the number it expects next. The variable
/// <c>success</c> of TransmitPacket and TransmitAck occurs only on an output arc
/// (a <em>free variable</em>), so each transition has two binding elements per packet:
/// one that delivers it and one that loses it.
/// </para>
/// </remarks>
public sealed class SimpleProtocol : CpnModel
{
    /// <summary>The book's colour set NOxDATA: a sequence number and a data chunk.</summary>
    public sealed record Packet(int No, string Data);

    public readonly Place<Packet> PacketsToSend, A, B;
    public readonly Place<int>    NextSend, NextRec, C, D;
    public readonly Place<string> DataReceived;

    public readonly Transition SendPacket, TransmitPacket, ReceivePacket, TransmitAck, ReceiveAck;

    /// <summary>The initial marking of PacketsToSend: "COL" "OUR" "ED " "PET" "RI " "NET".</summary>
    public static readonly Multiset<Packet> AllPackets = Multiset.Of(
        new Packet(1, "COL"), new Packet(2, "OUR"), new Packet(3, "ED "),
        new Packet(4, "PET"), new Packet(5, "RI "), new Packet(6, "NET"));

    public SimpleProtocol() : base("SimpleProtocol")
    {
        PacketsToSend = AddPlace("PacketsToSend", AllPackets);
        NextSend      = AddPlace("NextSend", Multiset.Of(1));
        A             = AddPlace<Packet>("A");
        B             = AddPlace<Packet>("B");
        NextRec       = AddPlace("NextRec", Multiset.Of(1));
        DataReceived  = AddPlace("DataReceived", Multiset.Of(""));
        C             = AddPlace<int>("C");
        D             = AddPlace<int>("D");

        var p        = new Var<Packet>("p");          // the book's (n, d)
        var q        = new Var<Packet>("q");
        var k        = new Var<int>("k");
        var n        = new Var<int>("n");
        var data     = new Var<string>("data");
        var success  = new Var<bool>("success");
        var success2 = new Var<bool>("success2");

        // The arc reading NextSend is deliberately declared *before* the arc that binds p:
        // arc order is irrelevant, as in CPN Tools.
        SendPacket = AddTransition("SendPacket")
            .Input(NextSend, () => p.Val.No)          // n on NextSend must equal the packet number
            .Input(PacketsToSend, p)                   // (n, d)
            .Output(PacketsToSend, p)
            .Output(NextSend, () => p.Val.No)
            .Output(A, p)
            .Build();

        TransmitPacket = AddTransition("TransmitPacket")
            .Input(A, p)
            .Output(B, () => success.Val ? Multiset.Of(p.Val) : Multiset.Empty<Packet>())
            .Build();

        ReceivePacket = AddTransition("ReceivePacket")
            .Input(B, q)
            .Input(NextRec, k)
            .Input(DataReceived, data)
            .Output(NextRec,      () => q.Val.No == k.Val ? k.Val + 1 : k.Val)
            .Output(DataReceived, () => q.Val.No == k.Val ? data.Val + q.Val.Data : data.Val)
            .Output(C,            () => q.Val.No == k.Val ? k.Val + 1 : k.Val)
            .Build();

        TransmitAck = AddTransition("TransmitAck")
            .Input(C, n)
            .Output(D, () => success2.Val ? Multiset.Of(n.Val) : Multiset.Empty<int>())
            .Build();

        ReceiveAck = AddTransition("ReceiveAck")
            .Input(D, n)
            .Input(NextSend, k)
            .Output(NextSend, n)
            .Build();
    }

    /// <summary>True once every packet has been delivered in order.</summary>
    public bool IsComplete => DataReceived.Marking.Count("COLOURED PETRI NET") == 1;
}
