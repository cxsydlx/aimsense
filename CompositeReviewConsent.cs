namespace AimSense;
internal sealed class CompositeReviewConsent
{
    public bool Requested { get; private set; }
    public bool Accepted { get; private set; }
    public void Reset() { Requested = Accepted = false; }
    public void Request() { Requested = true; Accepted = false; }
    public void Accept() { if (!Requested) throw new InvalidOperationException("尚未征求综合复核同意。"); Accepted = true; }
    public void RequireAccepted() { if (!Accepted) throw new InvalidOperationException("综合复核需要用户主动同意。"); }
}
