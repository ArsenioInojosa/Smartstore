﻿using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Products;

namespace Smartstore.Core.Catalog.Rules
{
    public class AttributeRuleContext
    {
        public AttributeRuleContext(
            Product product,
            ProductVariantAttribute attribute,
            ProductVariantAttributeCombination attributeCombination,
            IList<ProductVariantAttributeValue> selectedValues)
        {
            Product = Guard.NotNull(product);
            Attribute = Guard.NotNull(attribute);
            AttributeCombination = attributeCombination;
            SelectedValues = Guard.NotNull(selectedValues);
        }

        private int[] _selectedValueIds;

        /// <summary>
        /// Gets the product belonging to <see cref="Attribute"/>.
        /// </summary>
        public Product Product { get; }

        /// <summary>
        /// Gets the current product attribute to be checked.
        /// </summary>
        public ProductVariantAttribute Attribute { get; }

        /// <summary>
        /// Gets the current attribute combination. <c>null</c> if no attribute combination exists for the current attribute selection.
        /// </summary>
        public ProductVariantAttributeCombination AttributeCombination { get; }

        /// <summary>
        /// Gets the values of the currently selected product attributes.
        /// </summary>
        public IList<ProductVariantAttributeValue> SelectedValues { get; }

        /// <summary>
        /// Gets the identifiers of the selected product attribute values.
        /// Only includes list-type attributes (<see cref="ProductVariantAttribute.IsListTypeAttribute"/>).
        /// </summary>
        public int[] SelectedValueIds
        {
            get => _selectedValueIds ??= SelectedValues.Select(x => x.Id).ToArray();
        }
    }
}
