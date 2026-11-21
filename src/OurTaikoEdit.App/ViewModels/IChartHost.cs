namespace OurTaikoEdit.App.ViewModels;

// Where a chart that is not the person's own project goes: a website that opened its chart in the
// editor (the browser version inside net.ourtaiko.org). Saving hands the text to the host, which
// stores it its own way (and may refuse it); leaving goes back to the host's page.
public interface IChartHost
{
    // The text to store. Saved false with a message when the host did not take it (null message:
    // the person called it off), and the line the problem is on when there is one.
    Task<HostSaveResult> SaveAsync(string text);

    // Back to the host's page.
    void Leave();

    // Whether there are changes the host has not got: it may ask before its page is left.
    void DirtyChanged(bool dirty);
}

public sealed record HostSaveResult(bool Saved, string? Message = null, int? Line = null);
