using System.Text.RegularExpressions;

namespace MyScorer.Core;

public static partial class Validation
{
    [GeneratedRegex(@"^[a-zA-Z0-9\-]{1,20}$")]
    public static partial Regex IdPattern();
}
