using System;
using System.Linq;
using Centrifugal.Centrifuge;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for FilterNodeBuilder.
    /// </summary>
    public class FilterNodeBuilderTests
    {
        [Fact]
        public void Eq_CreatesEqualityFilterNode()
        {
            var filter = FilterNodeBuilder.Eq("ticker", "BTC");

            Assert.NotNull(filter);
            Assert.Equal("ticker", filter.InternalNode.Key);
            Assert.Equal("eq", filter.InternalNode.Cmp);
            Assert.Equal("BTC", filter.InternalNode.Val);
        }

        [Fact]
        public void Neq_CreatesInequalityFilterNode()
        {
            var filter = FilterNodeBuilder.Neq("status", "inactive");

            Assert.NotNull(filter);
            Assert.Equal("status", filter.InternalNode.Key);
            Assert.Equal("neq", filter.InternalNode.Cmp);
            Assert.Equal("inactive", filter.InternalNode.Val);
        }

        [Fact]
        public void In_CreatesSetInclusionFilterNode()
        {
            var filter = FilterNodeBuilder.In("ticker", "BTC", "ETH", "SOL");

            Assert.NotNull(filter);
            Assert.Equal("ticker", filter.InternalNode.Key);
            Assert.Equal("in", filter.InternalNode.Cmp);
            Assert.Equal(3, filter.InternalNode.Vals.Count);
            Assert.Contains("BTC", filter.InternalNode.Vals);
            Assert.Contains("ETH", filter.InternalNode.Vals);
            Assert.Contains("SOL", filter.InternalNode.Vals);
        }

        [Fact]
        public void In_HandlesEmptyValues()
        {
            var filter = FilterNodeBuilder.In("ticker");

            Assert.NotNull(filter);
            Assert.Equal("ticker", filter.InternalNode.Key);
            Assert.Equal("in", filter.InternalNode.Cmp);
            Assert.Empty(filter.InternalNode.Vals);
        }

        [Fact]
        public void Nin_CreatesSetExclusionFilterNode()
        {
            var filter = FilterNodeBuilder.Nin("status", "deleted", "archived");

            Assert.NotNull(filter);
            Assert.Equal("status", filter.InternalNode.Key);
            Assert.Equal("nin", filter.InternalNode.Cmp);
            Assert.Equal(2, filter.InternalNode.Vals.Count);
            Assert.Contains("deleted", filter.InternalNode.Vals);
            Assert.Contains("archived", filter.InternalNode.Vals);
        }

        [Fact]
        public void Ex_CreatesExistenceFilterNode()
        {
            var filter = FilterNodeBuilder.Ex("premium");

            Assert.NotNull(filter);
            Assert.Equal("premium", filter.InternalNode.Key);
            Assert.Equal("ex", filter.InternalNode.Cmp);
            Assert.Empty(filter.InternalNode.Val);
        }

        [Fact]
        public void Nex_CreatesNonExistenceFilterNode()
        {
            var filter = FilterNodeBuilder.Nex("deprecated");

            Assert.NotNull(filter);
            Assert.Equal("deprecated", filter.InternalNode.Key);
            Assert.Equal("nex", filter.InternalNode.Cmp);
            Assert.Empty(filter.InternalNode.Val);
        }

        [Fact]
        public void StartsWith_CreatesPrefixFilterNode()
        {
            var filter = FilterNodeBuilder.StartsWith("symbol", "BTC-");

            Assert.NotNull(filter);
            Assert.Equal("symbol", filter.InternalNode.Key);
            Assert.Equal("sw", filter.InternalNode.Cmp);
            Assert.Equal("BTC-", filter.InternalNode.Val);
        }

        [Fact]
        public void EndsWith_CreatesSuffixFilterNode()
        {
            var filter = FilterNodeBuilder.EndsWith("symbol", "-USD");

            Assert.NotNull(filter);
            Assert.Equal("symbol", filter.InternalNode.Key);
            Assert.Equal("ew", filter.InternalNode.Cmp);
            Assert.Equal("-USD", filter.InternalNode.Val);
        }

        [Fact]
        public void Contains_CreatesSubstringFilterNode()
        {
            var filter = FilterNodeBuilder.Contains("name", "crypto");

            Assert.NotNull(filter);
            Assert.Equal("name", filter.InternalNode.Key);
            Assert.Equal("ct", filter.InternalNode.Cmp);
            Assert.Equal("crypto", filter.InternalNode.Val);
        }

        [Fact]
        public void Lt_CreatesLessThanFilterNode()
        {
            var filter = FilterNodeBuilder.Lt("price", "50000");

            Assert.NotNull(filter);
            Assert.Equal("price", filter.InternalNode.Key);
            Assert.Equal("lt", filter.InternalNode.Cmp);
            Assert.Equal("50000", filter.InternalNode.Val);
        }

        [Fact]
        public void Lte_CreatesLessThanOrEqualFilterNode()
        {
            var filter = FilterNodeBuilder.Lte("price", "50000");

            Assert.NotNull(filter);
            Assert.Equal("price", filter.InternalNode.Key);
            Assert.Equal("lte", filter.InternalNode.Cmp);
            Assert.Equal("50000", filter.InternalNode.Val);
        }

        [Fact]
        public void Gt_CreatesGreaterThanFilterNode()
        {
            var filter = FilterNodeBuilder.Gt("volume", "1000");

            Assert.NotNull(filter);
            Assert.Equal("volume", filter.InternalNode.Key);
            Assert.Equal("gt", filter.InternalNode.Cmp);
            Assert.Equal("1000", filter.InternalNode.Val);
        }

        [Fact]
        public void Gte_CreatesGreaterThanOrEqualFilterNode()
        {
            var filter = FilterNodeBuilder.Gte("volume", "1000");

            Assert.NotNull(filter);
            Assert.Equal("volume", filter.InternalNode.Key);
            Assert.Equal("gte", filter.InternalNode.Cmp);
            Assert.Equal("1000", filter.InternalNode.Val);
        }

        [Fact]
        public void And_CombinesMultipleFiltersWithAndLogic()
        {
            var filter = FilterNodeBuilder.And(
                FilterNodeBuilder.Eq("ticker", "BTC"),
                FilterNodeBuilder.Gt("price", "50000")
            );

            Assert.NotNull(filter);
            Assert.Equal("and", filter.InternalNode.Op);
            Assert.Equal(2, filter.InternalNode.Nodes.Count);
            Assert.Equal("ticker", filter.InternalNode.Nodes[0].Key);
            Assert.Equal("eq", filter.InternalNode.Nodes[0].Cmp);
            Assert.Equal("price", filter.InternalNode.Nodes[1].Key);
            Assert.Equal("gt", filter.InternalNode.Nodes[1].Cmp);
        }

        [Fact]
        public void And_HandlesEmptyNodes()
        {
            var filter = FilterNodeBuilder.And();

            Assert.NotNull(filter);
            Assert.Equal("and", filter.InternalNode.Op);
            Assert.Empty(filter.InternalNode.Nodes);
        }

        [Fact]
        public void Or_CombinesMultipleFiltersWithOrLogic()
        {
            var filter = FilterNodeBuilder.Or(
                FilterNodeBuilder.Eq("ticker", "BTC"),
                FilterNodeBuilder.Eq("ticker", "ETH"),
                FilterNodeBuilder.Eq("ticker", "SOL")
            );

            Assert.NotNull(filter);
            Assert.Equal("or", filter.InternalNode.Op);
            Assert.Equal(3, filter.InternalNode.Nodes.Count);
            Assert.All(filter.InternalNode.Nodes, node => Assert.Equal("ticker", node.Key));
            Assert.All(filter.InternalNode.Nodes, node => Assert.Equal("eq", node.Cmp));
        }

        [Fact]
        public void Not_NegatesFilterCondition()
        {
            var innerFilter = FilterNodeBuilder.Eq("status", "deleted");
            var filter = FilterNodeBuilder.Not(innerFilter);

            Assert.NotNull(filter);
            Assert.Equal("not", filter.InternalNode.Op);
            Assert.Single(filter.InternalNode.Nodes);
            Assert.Equal("status", filter.InternalNode.Nodes[0].Key);
            Assert.Equal("eq", filter.InternalNode.Nodes[0].Cmp);
            Assert.Equal("deleted", filter.InternalNode.Nodes[0].Val);
        }

        [Fact]
        public void ComplexFilter_AndWithMultipleConditions()
        {
            var filter = FilterNodeBuilder.And(
                FilterNodeBuilder.Eq("ticker", "BTC"),
                FilterNodeBuilder.Gt("price", "50000"),
                FilterNodeBuilder.Lt("price", "100000")
            );

            Assert.NotNull(filter);
            Assert.Equal("and", filter.InternalNode.Op);
            Assert.Equal(3, filter.InternalNode.Nodes.Count);
        }

        [Fact]
        public void ComplexFilter_NestedAndOr()
        {
            var filter = FilterNodeBuilder.And(
                FilterNodeBuilder.Or(
                    FilterNodeBuilder.Eq("ticker", "BTC"),
                    FilterNodeBuilder.Eq("ticker", "ETH")
                ),
                FilterNodeBuilder.Gt("volume", "1000")
            );

            Assert.NotNull(filter);
            Assert.Equal("and", filter.InternalNode.Op);
            Assert.Equal(2, filter.InternalNode.Nodes.Count);
            Assert.Equal("or", filter.InternalNode.Nodes[0].Op);
            Assert.Equal(2, filter.InternalNode.Nodes[0].Nodes.Count);
            Assert.Equal("volume", filter.InternalNode.Nodes[1].Key);
        }

        [Fact]
        public void ComplexFilter_NotWithAnd()
        {
            var filter = FilterNodeBuilder.Not(
                FilterNodeBuilder.And(
                    FilterNodeBuilder.Eq("status", "deleted"),
                    FilterNodeBuilder.Eq("archived", "true")
                )
            );

            Assert.NotNull(filter);
            Assert.Equal("not", filter.InternalNode.Op);
            Assert.Single(filter.InternalNode.Nodes);
            Assert.Equal("and", filter.InternalNode.Nodes[0].Op);
            Assert.Equal(2, filter.InternalNode.Nodes[0].Nodes.Count);
        }

        [Fact]
        public void ComplexFilter_InWithOr()
        {
            var filter = FilterNodeBuilder.Or(
                FilterNodeBuilder.In("ticker", "BTC", "ETH"),
                FilterNodeBuilder.Gt("price", "100000")
            );

            Assert.NotNull(filter);
            Assert.Equal("or", filter.InternalNode.Op);
            Assert.Equal(2, filter.InternalNode.Nodes.Count);
            Assert.Equal("in", filter.InternalNode.Nodes[0].Cmp);
            Assert.Equal(2, filter.InternalNode.Nodes[0].Vals.Count);
        }

        [Fact]
        public void ComplexFilter_StringOperationsWithAnd()
        {
            var filter = FilterNodeBuilder.And(
                FilterNodeBuilder.StartsWith("symbol", "BTC"),
                FilterNodeBuilder.EndsWith("symbol", "USD"),
                FilterNodeBuilder.Contains("name", "futures")
            );

            Assert.NotNull(filter);
            Assert.Equal("and", filter.InternalNode.Op);
            Assert.Equal(3, filter.InternalNode.Nodes.Count);
            Assert.Equal("sw", filter.InternalNode.Nodes[0].Cmp);
            Assert.Equal("ew", filter.InternalNode.Nodes[1].Cmp);
            Assert.Equal("ct", filter.InternalNode.Nodes[2].Cmp);
        }

        [Fact]
        public void ComplexFilter_ExistenceChecksWithOr()
        {
            var filter = FilterNodeBuilder.Or(
                FilterNodeBuilder.Ex("premium"),
                FilterNodeBuilder.Nex("restricted")
            );

            Assert.NotNull(filter);
            Assert.Equal("or", filter.InternalNode.Op);
            Assert.Equal(2, filter.InternalNode.Nodes.Count);
            Assert.Equal("ex", filter.InternalNode.Nodes[0].Cmp);
            Assert.Equal("nex", filter.InternalNode.Nodes[1].Cmp);
        }
    }
}
