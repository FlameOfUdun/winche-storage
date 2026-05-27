using Winche.Sentinel.AspNetCore.Abstraction;
using Winche.Storage.Models;

namespace Winche.Storage.AspNetCore.Rest.Abstraction;

public abstract class FileClaimsAccessor : HttpCallerClaimsAccessor<FileRecord>;
