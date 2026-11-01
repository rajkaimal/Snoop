using System.Globalization;
using System.Text;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Comparing;
using Snoop.Inspection.Formatting;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>A diff line for display.</summary>
public sealed record CompareLine(DiffKind Kind, string Marker, string LeftNumber, string RightNumber, string Text);

/// <summary>
/// The Compare tab: two sessions side by side as one diff of their requests and responses, with
/// bodies decoded and pretty-printed so only real differences show.
/// </summary>
public sealed class CompareViewModel : ObservableObject
{
    private IReadOnlyList<CompareLine> _lines = new List<CompareLine>();
    private string _title = "Right-click a session → Mark for compare, then right-click another → Compare with marked.";
    private bool _onlyDifferences;
    private IReadOnlyList<DiffLine> _diff = [];

    public IReadOnlyList<CompareLine> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Hide lines that are the same on both sides.</summary>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (SetProperty(ref _onlyDifferences, value))
            {
                Lines = Present(_diff, value);
            }
        }
    }

    public void Compare(Session left, Session right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        _diff = LineDiff.Compare(Describe(left), Describe(right));
        var changes = LineDiff.CountChanges(_diff);
        Title = $"#{left.Id.ToString(CultureInfo.InvariantCulture)} (−) vs #{right.Id.ToString(CultureInfo.InvariantCulture)} (+): "
                + (changes == 0 ? "identical" : $"{changes.ToString("N0", CultureInfo.InvariantCulture)} line(s) differ");
        Lines = Present(_diff, _onlyDifferences);
    }

    /// <summary>The text that is compared: request and response, bodies decoded and pretty-printed.</summary>
    internal static string Describe(Session session)
    {
        var builder = new StringBuilder();
        var request = session.Request;
        builder.Append("REQUEST\n");
        builder.Append(request.Method).Append(' ').Append(request.Url.AbsoluteUri).Append(' ').Append(request.HttpVersion).Append('\n');
        AppendHeaders(builder, request.Headers);
        AppendBody(builder, request.Body, request.Headers);

        builder.Append("\nRESPONSE\n");
        if (session.Response is { } response)
        {
            builder.Append(response.HttpVersion).Append(' ').Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(response.ReasonPhrase).Append('\n');
            AppendHeaders(builder, response.Headers);
            AppendBody(builder, response.Body, response.Headers);
        }
        else
        {
            builder.Append(session.Error ?? "(no response)").Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendHeaders(StringBuilder builder, Core.Http.HttpHeaderList headers)
    {
        foreach (var header in headers)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        }
    }

    private static void AppendBody(StringBuilder builder, Core.Bodies.CapturedBody? body, Core.Http.HttpHeaderList headers)
    {
        if (body is null || body.TotalBytes == 0)
        {
            return;
        }

        var rendered = BodyRenderer.Render(body, headers);
        builder.Append('\n');
        builder.Append(rendered.Mode == BodyViewMode.Image ? $"[image, {rendered.Summary}]" : rendered.Text);
        builder.Append('\n');
    }

    private static IReadOnlyList<CompareLine> Present(IReadOnlyList<DiffLine> diff, bool onlyDifferences) =>
        diff.Where(l => !onlyDifferences || l.Kind != DiffKind.Same).Select(l => new CompareLine(
            l.Kind,
            l.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "−", _ => " " },
            l.LeftNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            l.RightNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            l.Text)).ToList();
}
