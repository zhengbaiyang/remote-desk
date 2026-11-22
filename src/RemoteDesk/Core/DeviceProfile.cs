namespace RemoteDesk;

internal sealed record DeviceProfile
{
    public DeviceProfile(string name, string id)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(id);

        name = name.Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new ArgumentException("请输入 1 到 40 个字符的电脑名称。", nameof(name));

        id = id.Trim();
        if (id.All(c => char.IsAsciiDigit(c) || char.IsWhiteSpace(c)))
            id = string.Concat(id.Where(char.IsAsciiDigit));

        if (id.Length is < 3 or > 32 || !char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("请输入目标电脑在 RustDesk 中显示的设备 ID。", nameof(id));

        Name = name;
        Id = id;
    }

    public string Name { get; }
    public string Id { get; }

    public static DeviceProfile Create(string name, string id) => new(name, id);
}
