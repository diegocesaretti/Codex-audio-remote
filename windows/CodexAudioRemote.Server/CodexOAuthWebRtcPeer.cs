using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

/// <summary>
/// Maintains the WebRTC leg required by Codex app-server when the user is
/// authenticated with ChatGPT OAuth. Audio still travels through the app-server
/// sideband so Android can keep using the existing PCM WebSocket protocol.
/// </summary>
internal sealed class CodexOAuthWebRtcPeer : IDisposable
{
    const string EventsDataChannelName = "oai-events";

    RTCPeerConnection? peerConnection;
    bool disposed;

    public async Task<string> CreateOfferAsync()
    {
        ThrowIfDisposed();
        CloseCurrentPeer("replaced");

        var peer = new RTCPeerConnection(null);
        peerConnection = peer;

        var audioTrack = new MediaStreamTrack(
            AudioCommonlyUsedFormats.OpusWebRTC,
            MediaStreamStatusEnum.SendRecv);
        peer.addTrack(audioTrack);

        var dataChannel = await peer.createDataChannel(EventsDataChannelName);
        dataChannel.onopen += () => Console.WriteLine("Codex OAuth WebRTC data channel connected");

        peer.onconnectionstatechange += state =>
            Console.WriteLine("Codex OAuth WebRTC peer: " + state);
        peer.oniceconnectionstatechange += state =>
            Console.WriteLine("Codex OAuth WebRTC ICE: " + state);

        var offer = peer.createOffer();
        await peer.setLocalDescription(offer);
        return offer.sdp;
    }

    public void ApplyAnswer(string sdp)
    {
        ThrowIfDisposed();
        var peer = peerConnection
            ?? throw new InvalidOperationException("Codex OAuth WebRTC offer was not created.");

        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdp
        });

        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException("Codex OAuth WebRTC rejected the SDP answer: " + result);
    }

    public void Close(string reason = "normal")
    {
        if (disposed) return;
        CloseCurrentPeer(reason);
    }

    void CloseCurrentPeer(string reason)
    {
        var peer = Interlocked.Exchange(ref peerConnection, null);
        if (peer is null) return;
        try { peer.Close(reason); }
        catch { }
        peer.Dispose();
    }

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CodexOAuthWebRtcPeer));
    }

    public void Dispose()
    {
        if (disposed) return;
        CloseCurrentPeer("disposed");
        disposed = true;
    }
}
