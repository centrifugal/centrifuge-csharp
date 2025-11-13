using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a filter node for server-side publication filtering based on tags.
    /// Use CentrifugeFilterNodeBuilder to construct filter expressions.
    /// </summary>
    public sealed class CentrifugeFilterNode
    {
        internal Protocol.FilterNode InternalNode { get; }

        internal CentrifugeFilterNode(Protocol.FilterNode node)
        {
            InternalNode = node;
        }
    }
}
