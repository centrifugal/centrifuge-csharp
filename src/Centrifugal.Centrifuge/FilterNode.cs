using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a filter node for server-side publication filtering based on tags.
    /// Use FilterNodeBuilder to construct filter expressions.
    /// </summary>
    public sealed class FilterNode
    {
        internal Protocol.FilterNode InternalNode { get; }

        internal FilterNode(Protocol.FilterNode node)
        {
            InternalNode = node;
        }
    }
}
