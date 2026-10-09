// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Avalonia.Layout
{
    internal struct UvMeasure : IEquatable<UvMeasure>
    {
        internal double U { get; set; }

        internal double V { get; set; }

        public UvMeasure(Orientation orientation, double width, double height)
        {
            if (orientation == Orientation.Horizontal)
            {
                U = width;
                V = height;
            }
            else
            {
                U = height;
                V = width;
            }
        }

        public readonly bool Equals(UvMeasure other)
        {
            return other.U.Equals(U) && other.V.Equals(V);
        }

        public override readonly bool Equals(object? obj)
        {
            return obj is UvMeasure measure && Equals(measure);
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(U, V);
        }

        public static bool operator ==(UvMeasure left, UvMeasure right) => left.Equals(right);
        public static bool operator !=(UvMeasure left, UvMeasure right) => !left.Equals(right);
    }
}
