namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Збережена добірка юзера (FR-21) — таблиця user_collections.
/// UserId — FK на asp_net_users(id) (Identity string-id), cascade.
/// </summary>
public class UserCollection
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<UserCollectionItem> Items { get; set; } = [];
}

/// <summary>
/// Елемент добірки — таблиця user_collection_items. PK(collection_id, title_id);
/// ord — позиція у добірці (порядок додавання). title_id bigint БЕЗ FK на titles:
/// видалення тайтла не руйнує добірку (компакт-форма просто пропускає відсутні id).
/// </summary>
public class UserCollectionItem
{
    public int CollectionId { get; set; }
    public UserCollection Collection { get; set; } = null!;
    public long TitleId { get; set; }
    public int Ord { get; set; }
}
