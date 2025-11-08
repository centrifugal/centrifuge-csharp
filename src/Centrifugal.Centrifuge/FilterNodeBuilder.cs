using System;
using System.Collections.Generic;
using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Helper class for building FilterNode expressions for server-side publication filtering.
    /// </summary>
    public static class FilterNodeBuilder
    {
        /// <summary>
        /// Creates a filter node that checks if a tag equals a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for equality comparison.</returns>
        public static FilterNode Eq(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "eq",
                Val = value
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag does not equal a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for inequality comparison.</returns>
        public static FilterNode Neq(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "neq",
                Val = value
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag value is in a set of values.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="values">The set of values to check against.</param>
        /// <returns>A FilterNode configured for set inclusion.</returns>
        public static FilterNode In(string key, params string[] values)
        {
            var node = new FilterNode
            {
                Key = key,
                Cmp = "in"
            };
            node.Vals.AddRange(values);
            return node;
        }

        /// <summary>
        /// Creates a filter node that checks if a tag value is not in a set of values.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="values">The set of values to check against.</param>
        /// <returns>A FilterNode configured for set exclusion.</returns>
        public static FilterNode Nin(string key, params string[] values)
        {
            var node = new FilterNode
            {
                Key = key,
                Cmp = "nin"
            };
            node.Vals.AddRange(values);
            return node;
        }

        /// <summary>
        /// Creates a filter node that checks if a tag exists.
        /// </summary>
        /// <param name="key">The tag key to check for existence.</param>
        /// <returns>A FilterNode configured to check tag existence.</returns>
        public static FilterNode Ex(string key)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "ex"
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag does not exist.
        /// </summary>
        /// <param name="key">The tag key to check for non-existence.</param>
        /// <returns>A FilterNode configured to check tag non-existence.</returns>
        public static FilterNode Nex(string key)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "nex"
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag value starts with a prefix.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="prefix">The prefix to check for.</param>
        /// <returns>A FilterNode configured for prefix matching.</returns>
        public static FilterNode StartsWith(string key, string prefix)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "sw",
                Val = prefix
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag value ends with a suffix.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="suffix">The suffix to check for.</param>
        /// <returns>A FilterNode configured for suffix matching.</returns>
        public static FilterNode EndsWith(string key, string suffix)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "ew",
                Val = suffix
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a tag value contains a substring.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="substring">The substring to check for.</param>
        /// <returns>A FilterNode configured for substring matching.</returns>
        public static FilterNode Contains(string key, string substring)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "ct",
                Val = substring
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a numeric tag value is less than a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for less-than comparison.</returns>
        public static FilterNode Lt(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "lt",
                Val = value
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a numeric tag value is less than or equal to a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for less-than-or-equal comparison.</returns>
        public static FilterNode Lte(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "lte",
                Val = value
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a numeric tag value is greater than a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for greater-than comparison.</returns>
        public static FilterNode Gt(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "gt",
                Val = value
            };
        }

        /// <summary>
        /// Creates a filter node that checks if a numeric tag value is greater than or equal to a value.
        /// </summary>
        /// <param name="key">The tag key to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A FilterNode configured for greater-than-or-equal comparison.</returns>
        public static FilterNode Gte(string key, string value)
        {
            return new FilterNode
            {
                Key = key,
                Cmp = "gte",
                Val = value
            };
        }

        /// <summary>
        /// Creates a logical AND filter node that combines multiple filter conditions.
        /// All child conditions must be true for the filter to match.
        /// </summary>
        /// <param name="nodes">The filter nodes to combine with AND logic.</param>
        /// <returns>A FilterNode configured for logical AND.</returns>
        public static FilterNode And(params FilterNode[] nodes)
        {
            var node = new FilterNode
            {
                Op = "and"
            };
            node.Nodes.AddRange(nodes);
            return node;
        }

        /// <summary>
        /// Creates a logical OR filter node that combines multiple filter conditions.
        /// At least one child condition must be true for the filter to match.
        /// </summary>
        /// <param name="nodes">The filter nodes to combine with OR logic.</param>
        /// <returns>A FilterNode configured for logical OR.</returns>
        public static FilterNode Or(params FilterNode[] nodes)
        {
            var node = new FilterNode
            {
                Op = "or"
            };
            node.Nodes.AddRange(nodes);
            return node;
        }

        /// <summary>
        /// Creates a logical NOT filter node that negates a filter condition.
        /// The filter matches when the child condition is false.
        /// </summary>
        /// <param name="filterNode">The filter node to negate.</param>
        /// <returns>A FilterNode configured for logical NOT.</returns>
        public static FilterNode Not(FilterNode filterNode)
        {
            var node = new FilterNode
            {
                Op = "not"
            };
            node.Nodes.Add(filterNode);
            return node;
        }
    }
}
