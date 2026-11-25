using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib.Models;

namespace UndertaleModLib
{
    /// <summary>
    /// Deterministic ordering for reference search results, as shown by the
    /// "Find all references" and "Find unreferenced assets" dialogs and written by their export.
    /// </summary>
    /// <remarks>
    /// Both searches assemble their results from unordered sources (dictionaries whose lists come from
    /// data iteration, and for unreferenced assets from merged <see cref="System.Threading.Tasks.Parallel"/>
    /// partitions), so the raw order differs between runs. Sorting here, at the single place where the
    /// results are turned into a tree, gives both dialogs and the export one stable order.
    /// </remarks>
    public static class ReferenceResultsOrdering
    {
        /// <summary>
        /// Sorts the result groups by name and each group's items by their position in the data's resource lists.
        /// </summary>
        /// <param name="results">The raw results to order.</param>
        /// <param name="data">The data file the items belong to. If <see langword="null"/>, no item is considered ordered by list position.</param>
        /// <returns>Groups without empty item lists, sorted as described in <see cref="Sort(Dictionary{string, List{object}}, Func{object, int})"/>.</returns>
        public static List<KeyValuePair<string, List<object>>> Sort(Dictionary<string, List<object>> results, UndertaleData data)
        {
            return Sort(results, CreateSourceIndexResolver(data));
        }

        /// <summary>
        /// Sorts the result groups by name and each group's items by a caller-supplied source list position.
        /// </summary>
        /// <param name="results">The raw results to order.</param>
        /// <param name="getSourceIndex">
        /// Returns the item's index in its source resource list, or a negative value if it has none.
        /// </param>
        /// <returns>
        /// Groups sorted by name (ordinal, ignoring case, with an ordinal tie-break), with empty groups removed.
        /// Items in each group are sorted by source list index, with items that have no such index last
        /// (in their original relative order). Strings are ordered by content, and tile references by tile ID.
        /// </returns>
        public static List<KeyValuePair<string, List<object>>> Sort(Dictionary<string, List<object>> results,
                                                                    Func<object, int> getSourceIndex)
        {
            if (results is null)
                throw new ArgumentNullException(nameof(results));
            if (getSourceIndex is null)
                throw new ArgumentNullException(nameof(getSourceIndex));

            ResultItemComparer comparer = new(getSourceIndex);

            List<KeyValuePair<string, List<object>>> groups = results
                .Where(x => x.Value is not null && x.Value.Count > 0)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new KeyValuePair<string, List<object>>(x.Key, x.Value.OrderBy(item => item, comparer).ToList()))
                .ToList();

            return groups;
        }

        /// <summary>
        /// Creates a resolver that looks up an item's index in the <see cref="UndertaleData"/> resource list
        /// of its type, caching a reference-to-index map per type on first use.
        /// </summary>
        /// <param name="data">The data file to look items up in. May be <see langword="null"/>.</param>
        /// <returns>An index resolver that returns a negative value for items without a source list.</returns>
        public static Func<object, int> CreateSourceIndexResolver(UndertaleData data)
        {
            if (data is null)
                return _ => -1;

            Dictionary<Type, Dictionary<object, int>> indexCache = new();

            return item =>
            {
                if (item is null || item is UndertaleString)
                    return -1;

                Type type = item.GetType();
                if (!typeof(UndertaleResource).IsAssignableFrom(type))
                    return -1;

                if (!indexCache.TryGetValue(type, out Dictionary<object, int> indices))
                {
                    indices = new(ReferenceEqualityComparer.Instance);
                    try
                    {
                        if (data[type] is IList list)
                        {
                            for (int i = 0; i < list.Count; i++)
                            {
                                if (list[i] is object listItem)
                                    indices.TryAdd(listItem, i);
                            }
                        }
                    }
                    catch (MissingMemberException)
                    {
                        // The data file has no resource list of this type; treat its items as unordered.
                    }
                    catch (NotSupportedException)
                    {
                        // Not an UndertaleResource; treat its items as unordered.
                    }

                    indexCache[type] = indices;
                }

                return indices.TryGetValue(item, out int index) ? index : -1;
            };
        }

        /// <summary>
        /// The kind of sort key an item has, in ascending sort priority.
        /// </summary>
        private enum ItemKeyKind
        {
            SourceListIndex = 0,
            Text = 1,
            TileID = 2,
            None = 3
        }

        private sealed class ResultItemComparer : IComparer<object>
        {
            private readonly Func<object, int> getSourceIndex;

            public ResultItemComparer(Func<object, int> getSourceIndex)
            {
                this.getSourceIndex = getSourceIndex;
            }

            public int Compare(object x, object y)
            {
                ItemKeyKind kindX = Classify(x, out long keyX);
                ItemKeyKind kindY = Classify(y, out long keyY);

                if (kindX != kindY)
                    return ((int)kindX).CompareTo((int)kindY);

                return kindX switch
                {
                    ItemKeyKind.SourceListIndex or ItemKeyKind.TileID => keyX.CompareTo(keyY),
                    ItemKeyKind.Text => StringComparer.Ordinal.Compare(((UndertaleString)x).Content, ((UndertaleString)y).Content),
                    _ => 0
                };
            }

            private ItemKeyKind Classify(object item, out long key)
            {
                key = 0;

                if (item is null)
                    return ItemKeyKind.None;
                if (item is UndertaleString)
                    return ItemKeyKind.Text;
                if (item is ValueTuple<UndertaleBackground, UndertaleBackground.TileID> tileReference)
                {
                    key = tileReference.Item2.ID;
                    return ItemKeyKind.TileID;
                }

                int index = getSourceIndex(item);
                if (index >= 0)
                {
                    key = index;
                    return ItemKeyKind.SourceListIndex;
                }

                return ItemKeyKind.None;
            }
        }
    }
}
