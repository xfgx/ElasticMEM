namespace ElasticMem.App;
/// <summary>No embedded credentials and no network transmission. Export a local ZIP instead.</summary>
public static class ReportUploader
{
    public const string DefaultUpdateSource="";
    public static string MachineId=>"local-only";
    public static Task UploadAsync(string name,string text,CancellationToken ct=default)=>Task.FromException(new NotSupportedException("Network report upload was removed. Export a local ZIP and send it manually."));
    public static Task UploadFileAsync(string path,int maxBytes=262144,CancellationToken ct=default)=>Task.FromException(new NotSupportedException("Network report upload was removed. Export logs locally."));
}
