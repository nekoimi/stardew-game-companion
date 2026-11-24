namespace StardewAI.Companion.Updates;

internal sealed record UpdateProgress(string Message, int? Percent = null)
{
    internal string Label => Percent is { } percent ? $"{Message} {percent}%" : Message;

    internal static Action Step(Action<UpdateProgress>? report, string message, int total)
    {
        if (total <= 0)
        {
            report?.Invoke(new(message, 100));
            return static () => { };
        }
        int completed = 0;
        int lastPercent = 0;
        report?.Invoke(new(message, 0));
        return () =>
        {
            if (report is null) return;
            int percent = ++completed * 100 / total;
            if (percent == lastPercent) return;
            lastPercent = percent;
            report(new(message, percent));
        };
    }
}
