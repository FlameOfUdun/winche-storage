namespace Winche.Storage.AspNetCore.Rest.Models;

public sealed record CompletePartItem(int PartNumber, string ETag);

public sealed record CompleteMultipartRequest(IEnumerable<CompletePartItem> Parts);
