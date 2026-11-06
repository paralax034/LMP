namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Generic pipeline value transformation methods.
/// </summary>
internal static class GenericExtensions
{
    extension<TIn>(TIn input)
    {
        /// <summary>
        /// Passes the value to a transformation function.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="transform"/> is null.</exception>
        public TOut Pipe<TOut>(Func<TIn, TOut> transform)
        {
            ArgumentNullException.ThrowIfNull(transform);
            return transform(input);
        }
    }
}