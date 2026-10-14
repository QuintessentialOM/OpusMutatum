using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpusMutatum {

	public static class GenExtensions {

		public static string ToNiceString<T>(this IEnumerable<T> l, string separator = ", ") {
			return "[" + string.Join(separator, l.Select(i => i.ToString()).ToArray()) + "]";
		}

		// On modern C# versions can just use SingleOrDefault(null) but not on this version apparently :))
		public static TSource SingleOrNull<TSource>(this IEnumerable<TSource> source) where TSource : class {
			return source.Count() == 0 ? null : source.Single();
		}
	}
}
