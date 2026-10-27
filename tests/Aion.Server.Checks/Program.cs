using Aion.Application.Characters;
using Aion.Core.Accounts;
using Aion.Core.Characters;

var account = new VerifiedAccount(Guid.NewGuid());
var own = new CharacterSummary(Guid.NewGuid(), account.AccountId, 1204, "Owned", 1);
var other = own with { AccountId = Guid.NewGuid() };
var repository = new StubRepository(own);
var service = new CharacterService(repository);

Check((await service.ListAsync(account)).Single() == own, "owned list");
Check(repository.LastAccount == account.AccountId, "repository receives authenticated scope");
Check(await service.FindAsync(account, own.CharacterId) == own, "owned lookup");
Check(await service.FindAsync(account, Guid.NewGuid()) is null, "wrong character ID rejected");
repository.Result = other;
Check(await service.FindAsync(account, own.CharacterId) is null, "foreign owner rejected");
try
{
    await service.ListAsync(account);
    throw new Exception("Foreign list must fail closed");
}
catch (InvalidOperationException) { }
try
{
    _ = new VerifiedAccount(Guid.Empty);
    throw new Exception("Empty principal must be rejected");
}
catch (ArgumentException) { }
Console.WriteLine("7 account/character boundary checks passed.");

// Customization bytes: version 12, then one byte per EAionPCCustomizeData_V12 field.
var blob = new byte[CharacterAppearance.V12Fields.Count + 1];
blob[0] = 12;
Array.Fill(blob, (byte)100, 1, blob.Length - 1);
void Set(string field, byte value) => blob[1 + CharacterAppearance.V12Fields.ToList().IndexOf(field)] = value;
Set("HairType", 188); Set("HeadType", 137); Set("BeardType", 101);
Set("C_HairColor_R", 144); Set("C_HairColor_G", 133); Set("C_HairColor_B", 120);
var appearance = CharacterAppearance.Decode(blob);
Check(CharacterAppearance.V12Fields.Count == 328 && appearance.Version == 12, "v12 layout");
Check(appearance.TypeIndex("HairType") == 88 && appearance.TypeIndex("HeadType") == 37, "type index = value - 100");
Check(appearance.TypeIndex("BeardType") == 1, "index 1 is the None entry");
Check(appearance.Color("HairColor") == "#908578", "colour channels");
Check(appearance.Fields["Height"] == 100, "sliders centre on 100");
Check(CharacterAppearance.Decode(blob[..100]).Fields.Count == 0, "unknown length is not guessed");
Check(CharacterAppearance.Decode([9, 1, 2]).Version == 9 && CharacterAppearance.Decode([]).Version == 0, "other versions");
Console.WriteLine("7 appearance decoding checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Check failed: " + name);
}

sealed class StubRepository(CharacterSummary result) : ICharacterRepository
{
    public CharacterSummary Result { get; set; } = result;
    public Guid LastAccount { get; private set; }
    public Task<IReadOnlyList<CharacterSummary>> ListOwnedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        LastAccount = accountId;
        return Task.FromResult<IReadOnlyList<CharacterSummary>>([Result]);
    }
    public Task<CharacterSummary?> FindOwnedAsync(Guid accountId, Guid characterId, CancellationToken cancellationToken)
    {
        LastAccount = accountId;
        return Task.FromResult<CharacterSummary?>(Result);
    }
}
