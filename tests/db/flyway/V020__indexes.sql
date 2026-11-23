CREATE INDEX idx_fantasy_squads_gameweek ON app.fantasy_squads(gameweek_id);
CREATE INDEX idx_points_results_gameweek ON app.points_results(gameweek_id);
CREATE INDEX idx_leaderboards_gameweek ON app.leaderboards(gameweek_id);
CREATE INDEX idx_fixtures_season ON football.fixtures(season_id);
CREATE INDEX idx_fixtures_competition ON football.fixtures(competition_id);
CREATE INDEX idx_player_match_stats_fixture ON football.player_match_stats(fixture_id);
CREATE INDEX idx_match_events_fixture ON football.match_events(fixture_id);
CREATE INDEX idx_sync_logs_job ON sync.sync_logs(sync_job_id);
