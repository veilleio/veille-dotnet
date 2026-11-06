namespace Veille;

/// <summary>Outcome of one batch item.</summary>
/// <typeparam name="T">Task result type.</typeparam>
/// <param name="Index">Position of the item in the input.</param>
/// <param name="Value">The task result when <see cref="Ok"/>.</param>
/// <param name="Error">What the task threw, or null.</param>
public sealed record BatchResult<T>(int Index, T? Value, Exception? Error)
{
    /// <summary>True when the task succeeded.</summary>
    public bool Ok => Error is null;
}
