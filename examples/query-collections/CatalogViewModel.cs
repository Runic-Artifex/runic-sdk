using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace QueryCollectionsExample;

/// <summary>A product of the catalog. Notes vary in length, so rows vary in height.</summary>
public sealed record Product(int Id, string Name, string Category, decimal Price, int Stock, string Notes);

/// <summary>The products, shared by every window: the stand-in for a database table.</summary>
public sealed class ProductStore
{
    private static readonly string[] Categories = ["Tools", "Garden", "Kitchen", "Lighting", "Storage", "Textiles"];
    private Product[] _products;

    public ProductStore(int count = 100_000)
    {
        var random = new Random(42);
        _products = [.. Enumerable.Range(1, count).Select(id => new Product(id, $"Product {id:D6}", Categories[id % Categories.Length],
            Math.Round((decimal)(random.NextDouble() * 500), 2), random.Next(0, 40),
            string.Join(' ', Enumerable.Repeat("Sturdy and reliable.", random.Next(1, 8)))))];
    }

    public IQueryable<Product> Products => Volatile.Read(ref _products).AsQueryable();

    public Product Restock(int id)
    {
        var products = (Product[])Volatile.Read(ref _products).Clone();
        var index = id - 1;
        products[index] = products[index] with { Stock = products[index].Stock + 10 };
        Volatile.Write(ref _products, products);
        return products[index];
    }

    public Product Add()
    {
        var products = Volatile.Read(ref _products);
        var added = new Product(products.Length + 1, $"Product {products.Length + 1:D6}", "Tools", 9.99m, 1, "New.");
        Volatile.Write(ref _products, [.. products, added]);
        return added;
    }
}

/// <summary>
/// One window's catalog. The rows stay in the store: each page loads the rows it shows, and sorts and filters with
/// setQuery. The query is this ViewModel's state, so every page of the window shares it.
/// </summary>
public sealed class CatalogViewModel : ReactiveObject, IDisposable
{
    private readonly ProductStore _store;

    public CatalogViewModel(ProductStore store, IRunicModelContext context)
    {
        _store = store;
        var fields = new RunicQueryFields<Product>()
            .Add("id", product => product.Id)
            .AddText("name", product => product.Name)
            .AddText("category", product => product.Category)
            .Add("price", product => product.Price)
            .Add("stock", product => product.Stock)
            .AddCustom("inStock", (products, _) => products.Where(product => product.Stock > 0));
        Products = new(new RunicQueryableRowSource<Product, int>(() => store.Products, fields, product => product.Id),
            product => product.Id, new RunicQueryCollectionOptions { ModelContext = context });
        var scheduler = ReactiveSchedulers.For(context);
        // A change in place keeps the row where it is: the page reloads only the page of rows that holds it.
        RestockCommand = ReactiveCommand.Create<int>(id => Products.Invalidate(_store.Restock(id).Id), scheduler);
        // A new row moves the rows after it: every page loads its range again, and the total is counted again.
        AddCommand = ReactiveCommand.Create(() => { _store.Add(); Products.Refresh(); }, scheduler);
    }

    /// <summary>Every product, as the page sorts and filters it.</summary>
    public RunicQueryCollection<Product, int> Products { get; }

    /// <summary>Adds ten to a product's stock.</summary>
    public ReactiveCommand<int, RxVoid> RestockCommand { get; }

    /// <summary>Adds a product at the end of the catalog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddCommand { get; }

    public void Dispose()
    {
        Products.Dispose();
        RestockCommand.Dispose();
        AddCommand.Dispose();
    }
}
