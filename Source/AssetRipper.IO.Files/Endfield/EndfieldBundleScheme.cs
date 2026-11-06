using AssetRipper.IO.Files.Streams.Smart;

namespace AssetRipper.IO.Files.Endfield;

public sealed class EndfieldBundleScheme : Scheme<EndfieldBundleFile>
{
	public override bool CanRead(SmartStream stream) => EndfieldBundleFile.IsEndfieldBundle(stream);
}
