using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.CatalogModule.Core.Events;
using VirtoCommerce.CatalogModule.Core.Model;
using VirtoCommerce.CatalogModule.Core.Services;
using VirtoCommerce.CatalogModule.Data.Caching;
using VirtoCommerce.CatalogModule.Data.Model;
using VirtoCommerce.CatalogModule.Data.Repositories;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Data.GenericCrud;

namespace VirtoCommerce.CatalogModule.Data.Services
{
    public class ItemService : CrudService<CatalogProduct, ItemEntity, ProductChangingEvent, ProductChangedEvent>, IItemService
    {
        private new readonly Func<ICatalogRepository> _repositoryFactory;
        private new readonly IEventPublisher _eventPublisher;
        private readonly AbstractValidator<IHasProperties> _hasPropertyValidator;
        private readonly ICatalogService _catalogService;
        private readonly ICategoryService _categoryService;
        private readonly IOutlineService _outlineService;
        private readonly IBlobUrlResolver _blobUrlResolver;
        private readonly ISkuGenerator _skuGenerator;
        private readonly AbstractValidator<CatalogProduct> _productValidator;

        public ItemService(
            Func<ICatalogRepository> catalogRepositoryFactory,
            IEventPublisher eventPublisher,
            AbstractValidator<IHasProperties> hasPropertyValidator,
            ICatalogService catalogService,
            ICategoryService categoryService,
            IOutlineService outlineService,
            IPlatformMemoryCache platformMemoryCache,
            IBlobUrlResolver blobUrlResolver,
            ISkuGenerator skuGenerator,
            AbstractValidator<CatalogProduct> productValidator)
            : base(catalogRepositoryFactory, platformMemoryCache, eventPublisher)
        {
            _repositoryFactory = catalogRepositoryFactory;
            _eventPublisher = eventPublisher;
            _hasPropertyValidator = hasPropertyValidator;
            _categoryService = categoryService;
            _outlineService = outlineService;
            _blobUrlResolver = blobUrlResolver;
            _skuGenerator = skuGenerator;
            _productValidator = productValidator;
            _catalogService = catalogService;
        }

        #region IItemService Members

        public virtual async Task<IList<CatalogProduct>> GetByCodes(string catalogId, IList<string> codes, string responseGroup)
        {
            var idsByCodes = await GetIdsByCodes(catalogId, codes);

            return idsByCodes.Any()
                ? await GetByIdsAsync(idsByCodes.Values.ToArray(), responseGroup, catalogId)
                : Array.Empty<CatalogProduct>();
        }

        public virtual async Task<IDictionary<string, string>> GetIdsByCodes(string catalogId, IList<string> codes)
        {
            var cacheKeyPrefix = CacheKey.With(GetType(), nameof(GetIdsByCodes), catalogId);

            var models = await _platformMemoryCache.GetOrLoadByIdsAsync(cacheKeyPrefix, codes,
                missingCodes => GetIdsByCodesNoCache(catalogId, missingCodes),
                ConfigureCache);

            return models.ToDictionary(x => x.Id, x => x.ProductId, StringComparer.OrdinalIgnoreCase);
        }

        public virtual async Task<CatalogProduct[]> GetByIdsAsync(string[] itemIds, string respGroup, string catalogId = null)
        {
            var products = await GetAsync(itemIds.ToList(), respGroup);

            // Remove outlines that don't belong to the requested catalog
            if (products.Any() && catalogId != null && HasFlag(respGroup, ItemResponseGroup.Outlines))
            {
                products
                    .Where(x => x.Variations != null)
                    .SelectMany(x => x.Variations)
                    .Concat(products)
                    .Apply(product =>
                    {
                        product.Outlines = product.Outlines
                            .Where(outline => outline.Items.Any(item =>
                                item.Id.EqualsInvariant(catalogId) &&
                                item.SeoObjectType.EqualsInvariant("catalog")))
                            .ToList();
                    });
            }

            return products.ToArray();
        }

        public virtual async Task<CatalogProduct> GetByIdAsync(string itemId, string responseGroup, string catalogId = null)
        {
            var products = await GetByIdsAsync(new[] { itemId }, responseGroup, catalogId);

            return products.FirstOrDefault();
        }

        public virtual Task SaveChangesAsync(CatalogProduct[] items)
        {
            return SaveChangesAsync(items.AsEnumerable());
        }

        public virtual Task DeleteAsync(string[] itemIds)
        {
            return DeleteAsync(itemIds, softDelete: false);
        }

        #endregion

        public override async Task DeleteAsync(IEnumerable<string> ids, bool softDelete = false)
        {
            var itemIds = ids.ToArray();
            var items = await GetByIdsAsync(itemIds, ItemResponseGroup.ItemInfo.ToString(), catalogId: null);

            if (items.Any())
            {
                var changedEntries = items.Select(x => new GenericChangedEntry<CatalogProduct>(x, EntryState.Deleted)).ToList();

                await _eventPublisher.Publish(new ProductChangingEvent(changedEntries));

                using (var repository = _repositoryFactory())
                {
                    // TODO: Implement soft delete
                    await repository.RemoveItemsAsync(itemIds);
                    await repository.UnitOfWork.CommitAsync();
                }

                ClearCache(items);

                await _eventPublisher.Publish(new ProductChangedEvent(changedEntries));
            }
        }

        public override async Task<IReadOnlyCollection<CatalogProduct>> GetAsync(List<string> ids, string responseGroup = null)
        {
            return await InternalGetAsync(ids, responseGroup, clone: true) as IReadOnlyCollection<CatalogProduct>;
        }

        /// <summary>
        /// Returns data from the cache without cloning. This consumes less memory, but returned data must not be modified.
        /// </summary>
        public virtual Task<IList<CatalogProduct>> GetNoCloneAsync(IList<string> ids, string responseGroup)
        {
            return InternalGetAsync(ids, responseGroup, clone: false);
        }


        protected virtual async Task<IList<CatalogProduct>> InternalGetAsync(IList<string> ids, string responseGroup, bool clone)
        {
            var cacheKeyPrefix = CacheKey.With(GetType(), nameof(InternalGetAsync), responseGroup);

            var models = await _platformMemoryCache.GetOrLoadByIdsAsync(cacheKeyPrefix, ids,
                missingIds => GetByIdsNoCache(missingIds, responseGroup),
                ConfigureCache);

            if (clone)
            {
                return models
                    .Select(x => x.CloneTyped())
                    .OrderBy(x => ids.IndexOf(x.Id))
                    .ToList();
            }

            return models
                .OrderBy(x => ids.IndexOf(x.Id))
                .ToList();
        }

        protected override async Task<IEnumerable<ItemEntity>> LoadEntities(IRepository repository, IEnumerable<string> ids, string responseGroup)
        {
            var entities = await ((ICatalogRepository)repository).GetItemByIdsAsync(ids.ToArray(), responseGroup);

            return entities;
        }

        protected override IEnumerable<CatalogProduct> ProcessModels(IEnumerable<ItemEntity> entities, string responseGroup)
        {
            var products = base.ProcessModels(entities, responseGroup).ToList();

            if (products.Any())
            {
                LoadDependencies(products).GetAwaiter().GetResult();
                ApplyInheritanceRules(products);

                var productsAndVariations = products
                    .Where(x => x.Variations != null)
                    .SelectMany(x => x.Variations)
                    .Concat(products)
                    .ToList();

                if (HasFlag(responseGroup, ItemResponseGroup.Outlines))
                {
                    _outlineService.FillOutlinesForObjects(productsAndVariations, catalogId: null);
                }

                foreach (var product in productsAndVariations)
                {
                    product.ReduceDetails(responseGroup);
                }
            }

            return products;
        }

        protected virtual bool HasFlag(string responseGroup, ItemResponseGroup flag)
        {
            var itemResponseGroup = EnumUtility.SafeParseFlags(responseGroup, ItemResponseGroup.ItemLarge);

            return itemResponseGroup.HasFlag(flag);
        }

        protected override async Task BeforeSaveChanges(IEnumerable<CatalogProduct> models)
        {
            var products = models.ToArray();
            await base.BeforeSaveChanges(products);
            await ValidateProductsAsync(products);
        }

        protected virtual async Task<IEnumerable<ProductCodeCacheItem>> GetIdsByCodesNoCache(string catalogId, IList<string> codes)
        {
            using var repository = _repositoryFactory();
            var query = repository.Items.Where(x => x.CatalogId == catalogId);

            query = codes.Count == 1
                ? query.Where(x => x.Code == codes.First())
                : query.Where(x => codes.Contains(x.Code));

            var items = await query
                .Select(x => new ProductCodeCacheItem { Id = x.Code, ProductId = x.Id })
                .ToListAsync();

            return items;
        }

        protected virtual void ConfigureCache(MemoryCacheEntryOptions cacheOptions, string id, ProductCodeCacheItem model)
        {
            cacheOptions.AddExpirationToken(CatalogCacheRegion.CreateChangeToken());
            cacheOptions.AddExpirationToken(ItemCacheRegion.CreateChangeTokenForKey(id));
        }

        protected override void ConfigureCache(MemoryCacheEntryOptions cacheOptions, string id, CatalogProduct model)
        {
            cacheOptions.AddExpirationToken(CatalogCacheRegion.CreateChangeToken());
            cacheOptions.AddExpirationToken(ItemCacheRegion.CreateChangeTokenForKey(id));

            if (model?.Variations?.Any() == true)
            {
                cacheOptions.AddExpirationToken(ItemCacheRegion.CreateChangeToken(model.Variations));
            }
        }

        protected override void ClearCache(IEnumerable<CatalogProduct> models)
        {
            GenericSearchCachingRegion<CatalogProduct>.ExpireRegion();

            AssociationSearchCacheRegion.ExpireRegion();
            SeoInfoCacheRegion.ExpireRegion();

            foreach (var model in models)
            {
                ItemCacheRegion.ExpireEntity(model);
            }
        }

        [Obsolete("Use LoadDependencies()")]
        public virtual Task LoadDependenciesAsync(IEnumerable<CatalogProduct> products)
        {
            return LoadDependencies(products.ToList());
        }

        protected virtual async Task LoadDependencies(IList<CatalogProduct> products)
        {
            // TODO: Refactor to do this by one call and iteration
            var catalogsIds = new { products }.GetFlatObjectsListWithInterface<IHasCatalogId>().Select(x => x.CatalogId).Where(x => x != null).Distinct().ToList();
            var catalogsByIdDict = (await _catalogService.GetNoCloneAsync(catalogsIds)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

            var categoriesIds = new { products }.GetFlatObjectsListWithInterface<IHasCategoryId>().Select(x => x.CategoryId).Where(x => x != null).Distinct().ToList();
            var categoriesByIdDict = (await _categoryService.GetNoCloneAsync(categoriesIds)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

            var allImages = new { products }.GetFlatObjectsListWithInterface<IHasImages>().Where(x => x.Images != null).SelectMany(x => x.Images);
            foreach (var image in allImages.Where(x => !string.IsNullOrEmpty(x.Url)))
            {
                image.RelativeUrl = !string.IsNullOrEmpty(image.RelativeUrl) ? image.RelativeUrl : image.Url;
                image.Url = _blobUrlResolver.GetAbsoluteUrl(image.Url);
            }

            var allAssets = new { products }.GetFlatObjectsListWithInterface<IHasAssets>().Where(x => x.Assets != null).SelectMany(x => x.Assets);
            foreach (var asset in allAssets.Where(x => !string.IsNullOrEmpty(x.Url)))
            {
                asset.RelativeUrl = !string.IsNullOrEmpty(asset.RelativeUrl) ? asset.RelativeUrl : asset.Url;
                asset.Url = _blobUrlResolver.GetAbsoluteUrl(asset.Url);
            }

            foreach (var product in products)
            {
                SetProductDependencies(product, catalogsByIdDict, categoriesByIdDict);

                if (product.MainProduct != null)
                {
                    SetProductDependencies(product.MainProduct, catalogsByIdDict, categoriesByIdDict);
                }
                if (!product.Variations.IsNullOrEmpty())
                {
                    foreach (var variation in product.Variations)
                    {
                        SetProductDependencies(variation, catalogsByIdDict, categoriesByIdDict);
                    }
                }
            }
        }

        protected virtual void SetProductDependencies(CatalogProduct product, IDictionary<string, Catalog> catalogsByIdDict, IDictionary<string, Category> categoriesByIdDict)
        {
            // TODO: Refactor after covering by the unit tests
            if (string.IsNullOrEmpty(product.Code))
            {
                product.Code = _skuGenerator.GenerateSku(product);
            }

            product.Catalog = catalogsByIdDict.GetValueOrThrow(product.CatalogId, $"catalog with key {product.CatalogId} doesn't exist");

            if (product.CategoryId != null)
            {
                product.Category = categoriesByIdDict.GetValueOrThrow(product.CategoryId, $"category with key {product.CategoryId} doesn't exist");
            }

            foreach (var link in product.Links ?? Array.Empty<CategoryLink>())
            {
                link.Catalog = catalogsByIdDict.GetValueOrThrow(link.CatalogId, $"link catalog with key {link.CatalogId} doesn't exist");

                if (!string.IsNullOrEmpty(link.CategoryId))
                {
                    link.Category = categoriesByIdDict.GetValueOrThrow(link.CategoryId, $"link category with key {link.CategoryId} doesn't exist");
                }
            }
        }

        protected virtual void ApplyInheritanceRules(IEnumerable<CatalogProduct> products)
        {
            foreach (var product in products)
            {
                if (product.MainProduct != null)
                {
                    // Need to apply inheritance rules for main product first.
                    product.MainProduct.TryInheritFrom(product.Category ?? (IEntity)product.Catalog);
                    product.TryInheritFrom(product.MainProduct);
                }
                else
                {
                    product.TryInheritFrom(product.Category ?? (IEntity)product.Catalog);
                }
            }
        }

        protected virtual async Task ValidateProductsAsync(CatalogProduct[] products)
        {
            if (products == null)
            {
                throw new ArgumentNullException(nameof(products));
            }

            // Validate products
            foreach (var product in products)
            {
                await _productValidator.ValidateAndThrowAsync(product);
            }

            // Validate properties
            foreach (var product in products)
            {
                var validationResult = await _hasPropertyValidator.ValidateAsync(product);
                if (!validationResult.IsValid)
                {
                    throw new ValidationException($"Product properties has validation error: {string.Join(Environment.NewLine, validationResult.Errors.Select(x => x.ToString()))}");
                }
            }
        }

        protected class ProductCodeCacheItem : Entity
        {
            public string ProductId { get; set; }
        }
    }
}
