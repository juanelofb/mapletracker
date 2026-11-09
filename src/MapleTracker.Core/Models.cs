using System.Text.RegularExpressions;

namespace MapleTracker.Core;

[Flags]
public enum MessageKind { Chat = 0, Buying = 1, Selling = 2, Item = 4 }
public sealed record FeedEntry(DateTimeOffset Time, string Text, MessageKind Kind, string? Item = null, long Quantity = 0);
public sealed record LootPickup(string Name, long Quantity);
public sealed record HudReading(int Level, long Experience);
public sealed record CaptureRegion(double X, double Y, double Width, double Height)
{
    public bool IsValid => X >= 0 && Y >= 0 && Width > 0.001 && Height > 0.001 && X + Width <= 1.00001 && Y + Height <= 1.00001;
    public override string ToString() => $"{X:P0}, {Y:P0} · {Width:P0} × {Height:P0}";
}

public sealed class TrackerSettings
{
    public string WindowTitle { get; set; } = "";
    public CaptureRegion Chat { get; set; } = new(.210, .916, .385, .035);
    public bool ChatHistoryOnly { get; set; }
    public CaptureRegion Loot { get; set; } = new(.85, .75, .15, .06);
    public CaptureRegion Level { get; set; } = new(.238, .969, .021, .022);
    public CaptureRegion Experience { get; set; } = new(.528, .958, .085, .019);
    public string[] BuyingTerms { get; set; } = ["Buying", "Buy", "B>", "WTB"];
    public string[] SellingTerms { get; set; } = ["Selling", "Sell", "S>", "WTS"];
    public string[] BuyingItems { get; set; } = [];
    public string[] SellingItems { get; set; } = [];
    public bool AlertsEnabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 500;
    public bool AlwaysOnTop { get; set; } = true;
    // Legacy shared value, used only when migrating older settings files.
    public double Opacity { get; set; } = .9;
    public double ChatOpacity { get; set; } = .9;
    public double StatisticsOpacity { get; set; } = .9;
    public double ChatLeft { get; set; } = 60;
    public double ChatTop { get; set; } = 80;
    public double ChatWidth { get; set; } = 700;
    public double ChatHeight { get; set; } = 630;
    public double StatsLeft { get; set; } = 790;
    public double StatsTop { get; set; } = 80;
    public double StatsWidth { get; set; } = 390;
    public double StatsHeight { get; set; } = 560;
}

public sealed class TradeClassifier(TrackerSettings settings)
{
    public MessageKind Classify(string text)
    {
        var message = TradeMessage.Parse(text);
        return TradeMessage.Markers(message.Body, settings).Aggregate(MessageKind.Chat, (kind, marker) => kind | marker.Kind);
    }
}
