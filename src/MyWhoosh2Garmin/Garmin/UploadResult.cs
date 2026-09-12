namespace MyWhoosh2Garmin.Garmin;

public enum UploadOutcome { Uploaded, Duplicate, Failed }

public record UploadResult(UploadOutcome Outcome, string? GarminActivityId, string? ErrorMessage);
