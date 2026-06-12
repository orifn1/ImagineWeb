#r "nuget: Microsoft.Data.Sqlite, 9.0.0"
using Microsoft.Data.Sqlite;
var conn = new SqliteConnection("Data Source=C:\\Repos\\My\\SiteDevelopmentTool\\src\\ImagineWeb.Api\\bin\\Debug\\net10.0\\hunter.db");
conn.Open();
var cmd = conn.CreateCommand();

// 1. Overall status breakdown
cmd.CommandText = "SELECT Status, COUNT(*) FROM Pages GROUP BY Status ORDER BY Status";
var r = cmd.ExecuteReader();
Console.WriteLine("=== STATUS BREAKDOWN ===");
var statusNames = new[]{"Discovered","Scraping","Scraped","Queued","Analyzing","Analyzed","Failed","Skipped","Dismissed"};
int total = 0;
while(r.Read()) {
    var st = (int)r.GetInt64(0);
    var cnt = (int)r.GetInt64(1);
    total += cnt;
    var name = st < statusNames.Length ? statusNames[st] : $"Unknown({st})";
    Console.WriteLine($"  {st} ({name}): {cnt}");
}
Console.WriteLine($"  TOTAL: {total}");
r.Close();

// 2. For analyzed pages: score distribution (both axes)
cmd.CommandText = @"SELECT 
    CASE WHEN InterestingnessScore > ProfitScore THEN InterestingnessScore ELSE ProfitScore END as BestScore,
    COUNT(*) 
FROM Pages WHERE Status = 5 
GROUP BY BestScore ORDER BY BestScore DESC";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== ANALYZED: BEST SCORE DISTRIBUTION ===");
while(r.Read()) Console.WriteLine($"  BestScore={r.GetInt64(0)}: {r.GetInt64(1)} pages");
r.Close();

// 3. All pages with bestScore >= 7 (full detail)
cmd.CommandText = @"SELECT Id, Title, Url, InterestingnessScore, ProfitScore, Phase2Skipped, FeasibilityScore, 
    ShouldDeepDive, OpportunityType, SiteConcept, ActionPlan, EstimatedEffort, EstimatedReward,
    CASE WHEN InterestingnessScore > ProfitScore THEN InterestingnessScore ELSE ProfitScore END as BestScore
FROM Pages WHERE Status = 5 AND (InterestingnessScore >= 7 OR ProfitScore >= 7) 
ORDER BY (CASE WHEN InterestingnessScore > ProfitScore THEN InterestingnessScore ELSE ProfitScore END) DESC, InterestingnessScore DESC";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== HIGH VALUE PAGES (bestScore >= 7) ===");
while(r.Read()) {
    Console.WriteLine($"\n  ID:{r.GetInt64(0)} | I:{r.GetInt64(3)} P:{r.GetInt64(4)} Best:{r.GetInt64(13)} | Phase2Skipped:{(r.IsDBNull(5)?"0":r.GetInt64(5).ToString())} Feas:{(r.IsDBNull(6)?"0":r.GetInt64(6).ToString())}");
    Console.WriteLine($"    Title: {(r.IsDBNull(1)?"?":r.GetString(1))}");
    Console.WriteLine($"    URL: {(r.IsDBNull(2)?"?":r.GetString(2))}");
    Console.WriteLine($"    Type: {(r.IsDBNull(8)?"?":r.GetString(8))}");
    var concept = r.IsDBNull(9) ? "NULL" : r.GetString(9);
    Console.WriteLine($"    Concept: {concept.Substring(0, Math.Min(150, concept.Length))}...");
    Console.WriteLine($"    ActionPlan: {(r.IsDBNull(10)?"NULL":r.GetString(10).Substring(0, Math.Min(100, r.GetString(10).Length)))}");
    Console.WriteLine($"    Effort: {(r.IsDBNull(11)?"NULL":r.GetString(11))} | Reward: {(r.IsDBNull(12)?"NULL":r.GetString(12))}");
}
r.Close();

// 4. Scan timing
cmd.CommandText = "SELECT MAX(AnalyzedAt), MAX(DiscoveredAt), MIN(DiscoveredAt) FROM Pages";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== SCAN TIMING ===");
if(r.Read()) {
    Console.WriteLine($"  Last analyzed: {(r.IsDBNull(0)?"NULL":r.GetString(0))}");
    Console.WriteLine($"  Last discovered: {(r.IsDBNull(1)?"NULL":r.GetString(1))}");
    Console.WriteLine($"  First discovered: {(r.IsDBNull(2)?"NULL":r.GetString(2))}");
}
r.Close();

// 5. Failed pages count & sample
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE Status = 6";
r = cmd.ExecuteReader(); r.Read();
Console.WriteLine($"\n=== FAILED PAGES: {r.GetInt64(0)} total ===");
r.Close();

// 6. Pages with Phase2Skipped = 1
cmd.CommandText = "SELECT Id, Title, InterestingnessScore, ProfitScore, FeasibilityScore FROM Pages WHERE Phase2Skipped = 1";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== PAGES WITH Phase2Skipped=1 (Phase2 attempted but failed) ===");
while(r.Read()) {
    Console.WriteLine($"  ID:{r.GetInt64(0)} I:{r.GetInt64(2)} P:{r.GetInt64(3)} Feas:{r.GetInt64(4)} | {(r.IsDBNull(1)?"?":r.GetString(1))}");
}
r.Close();

// 7. Successful Phase2
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE FeasibilityScore > 0";
r = cmd.ExecuteReader(); r.Read();
Console.WriteLine($"\n=== Successful Phase2 (FeasibilityScore > 0): {r.GetInt64(0)} ===");
r.Close();

// 8. Opportunity type distribution for successful pages
cmd.CommandText = "SELECT OpportunityType, COUNT(*), AVG(InterestingnessScore), AVG(ProfitScore) FROM Pages WHERE Status=5 AND InterestingnessScore > 0 GROUP BY OpportunityType ORDER BY COUNT(*) DESC";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== OPPORTUNITY TYPE DISTRIBUTION ===");
while(r.Read()) Console.WriteLine($"  {(r.IsDBNull(0)?"None":r.GetString(0))}: {r.GetInt64(1)} pages (avg I:{r.GetDouble(2):F1} P:{r.GetDouble(3):F1})");
r.Close();

// 9. Topics performance
cmd.CommandText = "SELECT Query, PagesFound, HighValueFinds FROM Topics ORDER BY HighValueFinds DESC LIMIT 15";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== TOP TOPICS (by HighValueFinds) ===");
while(r.Read()) {
    Console.WriteLine($"  [{r.GetInt64(2)} HV / {r.GetInt64(1)} found] {r.GetString(0)}");
}
r.Close();

// 10. Summary: total Phase1 attempts vs success
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE Status IN (5, 6)";
r = cmd.ExecuteReader(); r.Read();
var attempted = r.GetInt64(0);
r.Close();
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE Status = 5";
r = cmd.ExecuteReader(); r.Read();
var succeeded = r.GetInt64(0);
r.Close();
Console.WriteLine($"\n=== PHASE1 SUCCESS RATE: {succeeded}/{attempted} = {(attempted>0? (100.0*succeeded/attempted):0):F1}% ===");

// 11. High-value pages that scored > 7 on EITHER axis (these should trigger Phase2)
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE Status=5 AND (InterestingnessScore > 7 OR ProfitScore > 7)";
r = cmd.ExecuteReader(); r.Read();
Console.WriteLine($"\n=== Pages qualifying for Phase2 (bestScore > 7): {r.GetInt64(0)} ===");
r.Close();
