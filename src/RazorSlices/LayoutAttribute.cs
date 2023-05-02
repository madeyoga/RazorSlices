namespace RazorSlices;

/// <summary>
/// 
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class LayoutAttribute : Attribute
{
    private readonly string _identifier;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="identifier"></param>
    public LayoutAttribute(string identifier)
    {
        _identifier = identifier;
    }

    /// <summary>
    /// 
    /// </summary>
    public string Identifier => _identifier;
}