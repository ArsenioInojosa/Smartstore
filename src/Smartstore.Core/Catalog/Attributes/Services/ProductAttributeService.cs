﻿using Smartstore.Collections;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Catalog.Rules;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Data;
using Smartstore.Data.Hooks;

namespace Smartstore.Core.Catalog.Attributes
{
    public partial class ProductAttributeService : IProductAttributeService
    {
        private readonly SmartDbContext _db;
        private readonly ILocalizedEntityService _localizedEntityService;
        private readonly Lazy<IRuleProviderFactory> _ruleProviderFactory;

        public ProductAttributeService(
            SmartDbContext db,
            ILocalizedEntityService localizedEntityService,
            Lazy<IRuleProviderFactory> ruleProviderFactory)
        {
            _db = db;
            _localizedEntityService = localizedEntityService;
            _ruleProviderFactory = ruleProviderFactory;
        }

        public virtual async Task<Multimap<string, int>> GetExportFieldMappingsAsync(string fieldPrefix)
        {
            Guard.NotEmpty(fieldPrefix);

            var result = new Multimap<string, int>(StringComparer.OrdinalIgnoreCase);

            if (!fieldPrefix.EndsWith(':'))
            {
                fieldPrefix += ":";
            }

            var mappings = await _db.ProductAttributes
                .Where(x => !string.IsNullOrEmpty(x.ExportMappings))
                .Select(x => new
                {
                    x.Id,
                    x.ExportMappings
                })
                .ToListAsync();

            foreach (var mapping in mappings)
            {
                var rows = mapping.ExportMappings.SplitSafe(Environment.NewLine)
                    .Where(x => x.StartsWith(fieldPrefix, StringComparison.InvariantCultureIgnoreCase));

                foreach (var row in rows)
                {
                    var exportFieldName = row[fieldPrefix.Length..].TrimEnd();
                    if (exportFieldName.HasValue())
                    {
                        result.Add(exportFieldName, mapping.Id);
                    }
                }
            }

            return result;
        }

        public virtual async Task<int> CopyAttributeOptionsAsync(
            ProductVariantAttribute productVariantAttribute,
            int productAttributeOptionsSetId,
            bool deleteExistingValues)
        {
            Guard.NotNull(productVariantAttribute);
            Guard.NotZero(productVariantAttribute.Id);
            Guard.NotZero(productAttributeOptionsSetId);

            var clearLocalizedEntityCache = false;
            var pvavName = nameof(ProductVariantAttributeValue);

            if (deleteExistingValues)
            {
                await _db.LoadCollectionAsync(productVariantAttribute, x => x.ProductVariantAttributeValues);

                var valueIds = productVariantAttribute.ProductVariantAttributeValues.Select(x => x.Id).ToArray();
                if (valueIds.Length > 0)
                {
                    _db.ProductVariantAttributeValues.RemoveRange(productVariantAttribute.ProductVariantAttributeValues);
                    await _db.SaveChangesAsync();

                    var oldLocalizedProperties = await _localizedEntityService.GetLocalizedPropertyCollectionAsync(pvavName, valueIds);
                    if (oldLocalizedProperties.Count > 0)
                    {
                        clearLocalizedEntityCache = true;
                        _db.LocalizedProperties.RemoveRange(oldLocalizedProperties);
                        await _db.SaveChangesAsync();
                    }
                }
            }

            var optionsToCopy = await _db.ProductAttributeOptions
                .AsNoTracking()
                .Where(x => x.ProductAttributeOptionsSetId == productAttributeOptionsSetId)
                .ToListAsync();

            if (optionsToCopy.Count == 0)
            {
                return 0;
            }

            var newValues = new Dictionary<int, ProductVariantAttributeValue>();
            var existingValueNames = await _db.ProductVariantAttributeValues
                .Where(x => x.ProductVariantAttributeId == productVariantAttribute.Id)
                .Select(x => x.Name)
                .ToListAsync();

            foreach (var option in optionsToCopy)
            {
                if (!existingValueNames.Contains(option.Name))
                {
                    var pvav = option.Clone();
                    pvav.ProductVariantAttributeId = productVariantAttribute.Id;
                    newValues[option.Id] = pvav;
                }
            }

            if (newValues.Count == 0)
            {
                return 0;
            }

            // Save because we need the primary keys.
            await _db.ProductVariantAttributeValues.AddRangeAsync(newValues.Select(x => x.Value));
            await _db.SaveChangesAsync();

            var localizations = await _localizedEntityService.GetLocalizedPropertyCollectionAsync(nameof(ProductAttributeOption), newValues.Select(x => x.Key).ToArray());
            if (localizations.Count > 0)
            {
                clearLocalizedEntityCache = true;
                var localizationsMap = localizations.ToMultimap(x => x.EntityId, x => x);

                foreach (var option in optionsToCopy)
                {
                    if (newValues.TryGetValue(option.Id, out var value) && localizationsMap.TryGetValues(option.Id, out var props))
                    {
                        foreach (var prop in props)
                        {
                            _db.LocalizedProperties.Add(new()
                            {
                                EntityId = value.Id,
                                LocaleKeyGroup = pvavName,
                                LocaleKey = prop.LocaleKey,
                                LocaleValue = prop.LocaleValue,
                                LanguageId = prop.LanguageId,
                                IsHidden = prop.IsHidden,
                                CreatedOnUtc = DateTime.UtcNow,
                                UpdatedOnUtc = prop.UpdatedOnUtc,
                                CreatedBy = prop.CreatedBy,
                                UpdatedBy = prop.UpdatedBy
                            });
                        }
                    }
                }

                await _db.SaveChangesAsync();
            }

            if (clearLocalizedEntityCache)
            {
                await _localizedEntityService.ClearCacheAsync();
            }

            return newValues.Count;
        }

        public virtual Task<ICollection<int>> GetAttributeCombinationFileIdsAsync(Product product)
        {
            Guard.NotNull(product);

            if (!_db.IsCollectionLoaded(product, x => x.ProductVariantAttributeCombinations))
            {
                return GetAttributeCombinationFileIdsAsync(product.Id);
            }

            var fileIds = product.ProductVariantAttributeCombinations
                .Where(x => !string.IsNullOrEmpty(x.AssignedMediaFileIds) && x.IsActive)
                .Select(x => x.AssignedMediaFileIds)
                .ToList();

            return Task.FromResult(CreateFileIdSet(fileIds));
        }

        public virtual async Task<ICollection<int>> GetAttributeCombinationFileIdsAsync(int productId)
        {
            if (productId == 0)
            {
                return new HashSet<int>();
            }

            var fileIds = await _db.ProductVariantAttributeCombinations
                .Where(x => x.ProductId == productId && !string.IsNullOrEmpty(x.AssignedMediaFileIds) && x.IsActive)
                .Select(x => x.AssignedMediaFileIds)
                .ToListAsync();

            return CreateFileIdSet(fileIds);
        }

        private static ICollection<int> CreateFileIdSet(List<string> source)
        {
            if (source.Count == 0)
            {
                return new HashSet<int>();
            }

            var result = source
                .SelectMany(x => x.SplitSafe(','))
                .Select(x => x.ToInt())
                .Where(x => x != 0);

            return new HashSet<int>(result);
        }

        public virtual async Task<int> CreateAllAttributeCombinationsAsync(int productId)
        {
            if (productId == 0)
            {
                return 0;
            }

            // Delete all existing combinations for this product.
            await _db.ProductVariantAttributeCombinations
                .Where(x => x.ProductId == productId)
                .ExecuteDeleteAsync();

            var attributes = await _db.ProductVariantAttributes
                .AsNoTracking()
                .Include(x => x.ProductVariantAttributeValues)
                .Where(x => x.ProductId == productId)
                .ApplyListTypeFilter()
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync();

            if (attributes.Count == 0)
            {
                return 0;
            }

            var mappedAttributes = attributes
                .SelectMany(x => x.ProductVariantAttributeValues)
                .ToDictionarySafe(x => x.Id, x => x.ProductVariantAttribute);

            var toCombine = new List<List<ProductVariantAttributeValue>>();
            var resultMatrix = new List<List<ProductVariantAttributeValue>>();
            var tmpValues = new List<ProductVariantAttributeValue>();

            attributes
                .Where(x => x.ProductVariantAttributeValues.Any())
                .Each(x => toCombine.Add(x.ProductVariantAttributeValues.ToList()));

            if (toCombine.Count == 0)
            {
                return 0;
            }

            CombineAll(0, tmpValues);

            var numAdded = 0;

            using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important))
            {
                foreach (var values in resultMatrix)
                {
                    var attributeSelection = new ProductVariantAttributeSelection(string.Empty);

                    foreach (var value in values)
                    {
                        attributeSelection.AddAttributeValue(mappedAttributes[value.Id].Id, value.Id);
                    }

                    _db.ProductVariantAttributeCombinations.Add(new()
                    {
                        ProductId = productId,
                        RawAttributes = attributeSelection.AsJson(),
                        StockQuantity = 10000,
                        AllowOutOfStockOrders = true,
                        IsActive = true
                    });

                    if ((++numAdded % 100) == 0)
                    {
                        await scope.CommitAsync();
                    }
                }

                await scope.CommitAsync();
            }

            //foreach (var y in resultMatrix)
            //{
            //	var sb = new System.Text.StringBuilder();
            //	foreach (var x in y)
            //	{
            //		sb.AppendFormat("{0} ", x.Name);
            //	}
            //	sb.ToString().Dump();
            //}

            return numAdded;

            void CombineAll(int row, List<ProductVariantAttributeValue> tmp)
            {
                var combine = toCombine[row];

                for (var col = 0; col < combine.Count; ++col)
                {
                    var lst = new List<ProductVariantAttributeValue>(tmp)
                    {
                        combine[col]
                    };

                    if (row == (toCombine.Count - 1))
                    {
                        resultMatrix.Add(lst);
                    }
                    else
                    {
                        CombineAll(row + 1, lst);
                    }
                }
            }
        }

        public virtual async Task<int> CopyAttributesAsync(int sourceProductId, int targetProductId)
        {
            Guard.NotZero(sourceProductId);
            Guard.NotZero(targetProductId);

            if (sourceProductId == targetProductId)
            {
                return 0;
            }

            var existingAttributeIds = await _db.ProductVariantAttributes
                .Where(x => x.ProductId == targetProductId)
                .Select(x => x.ProductAttributeId)
                .ToArrayAsync();

            var sourceAttributes = (await _db.ProductVariantAttributes
                .AsNoTracking()
                .AsSplitQuery()
                .Include(x => x.ProductAttribute)
                .Include(x => x.ProductVariantAttributeValues)
                .Include(x => x.RuleSet)
                .ThenInclude(x => x.Rules)
                .Where(x => x.ProductId == sourceProductId && !existingAttributeIds.Contains(x.ProductAttributeId))
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync())
                .ToDictionarySafe(x => x.ProductAttributeId, x => x);
            if (sourceAttributes.Count == 0)
            {
                return 0;
            }

            // Maps old entity ID to new entity.
            var newAttributes = new Dictionary<int, ProductVariantAttribute>();
            var newValues = new Dictionary<int, ProductVariantAttributeValue>();
            var newRuleSets = new Dictionary<int, RuleSetEntity>();
            var newRules = new List<RuleEntity>();
            var clearLocalizedEntityCache = false;

            // Copy attributes.
            foreach (var pair in sourceAttributes)
            {
                var newAttribute = pair.Value.Clone();
                newAttribute.ProductId = targetProductId;
                newAttributes[pair.Key] = newAttribute;
            }

            _db.ProductVariantAttributes.AddRange(newAttributes.Select(x => x.Value));
            await _db.SaveChangesAsync();

            // Copy attribute values.
            foreach (var pair in newAttributes)
            {
                var newAttribute = pair.Value;
                if (!newAttribute.IsTransientRecord() && sourceAttributes.TryGetValue(pair.Key, out var source))
                {
                    foreach (var value in source.ProductVariantAttributeValues)
                    {
                        var newValue = value.Clone();
                        newValue.ProductVariantAttributeId = newAttribute.Id;
                        newValues[value.Id] = newValue;
                    }

                    if (source.RuleSet != null && !source.RuleSet.Rules.IsNullOrEmpty())
                    {
                        newRuleSets[pair.Key] = new RuleSetEntity
                        {
                            Scope = RuleScope.ProductAttribute,
                            IsActive = true,
                            IsSubGroup = false,
                            LogicalOperator = LogicalRuleOperator.And,
                            ProductVariantAttributeId = newAttribute.Id
                        };
                    }
                }
            }

            if (newValues.Count > 0)
            {
                _db.ProductVariantAttributeValues.AddRange(newValues.Select(x => x.Value));
                await _db.SaveChangesAsync();

                var localizations = await _localizedEntityService.GetLocalizedPropertyCollectionAsync(nameof(ProductVariantAttributeValue), newValues.Select(x => x.Key).ToArray());
                var newLocalizations = localizations
                    .Select(x =>
                    {
                        if (newValues.TryGetValue(x.EntityId, out var newValue))
                        {
                            var newLocalization = x.Clone();
                            newLocalization.EntityId = newValue.Id;
                            return newLocalization;
                        }
                        return null;
                    })
                    .Where(x => x != null)
                    .ToList();

                if (newLocalizations.Count > 0)
                {
                    _db.LocalizedProperties.AddRange(newLocalizations);
                    await _db.SaveChangesAsync();
                    clearLocalizedEntityCache = true;
                }
            }

            // Copy rule sets and rules.
            if (newRuleSets.Count > 0)
            {
                var ruleProvider = _ruleProviderFactory.Value.GetProvider(RuleScope.ProductAttribute, new AttributeRuleProviderContext(sourceProductId));
                var descriptors = await ruleProvider.GetRuleDescriptorsAsync();

                _db.RuleSets.AddRange(newRuleSets.Select(x => x.Value));
                await _db.SaveChangesAsync();

                foreach (var pair in newRuleSets)
                {
                    var newRuleSet = pair.Value;
                    if (!newRuleSet.IsTransientRecord() && sourceAttributes.TryGetValue(pair.Key, out var source))
                    {
                        newRules.AddRange(source.RuleSet.Rules
                            .Select(x =>
                            {
                                var newRule = x.Clone();
                                newRule.RuleSetId = newRuleSet.Id;

                                // Migrate rule value. It contains the IDs of ProductVariantAttributeValue.
                                return MigrateRule(newRule, newValues, descriptors);
                            }));
                    }
                }

                if (newRules.Count > 0)
                {
                    _db.Rules.AddRange(newRules);
                    await _db.SaveChangesAsync();
                }
            }

            if (clearLocalizedEntityCache)
            {
                await _localizedEntityService.ClearCacheAsync();
            }

            return newAttributes.Count;
        }

        protected virtual RuleEntity MigrateRule(RuleEntity rule, Dictionary<int, ProductVariantAttributeValue> newValueMap, RuleDescriptorCollection descriptors)
        {
            // TODO: (mg)(rules) required, do not comment-out! Move code to module.
            //if (descriptors.FindDescriptor(rule.RuleType) is AttributeRuleDescriptor descriptor
            //    && descriptor.ProcessorType == typeof(ProductAttributeRule))
            //{
            //    var valueIds = rule.Value
            //        .ToIntArray()
            //        .Select(x => newValueMap.TryGetValue(x, out var newValue) ? newValue.Id : 0)
            //        .Where(x => x != 0)
            //        .ToArray();

            //    rule.Value = valueIds.Length > 0 ? string.Join(',', valueIds) : null;
            //}

            return rule;
        }
    }
}
