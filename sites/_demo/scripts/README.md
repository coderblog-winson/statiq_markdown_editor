Demo site ships with all three standard scripts:

| Script | What it does | Out of the box |
|---|---|---|
| `preview.sh` | Spawns `python3 -m http.server` on port 5080 to serve `output/` | ✅ Just run it |
| `git_sync.sh` | Commits + pushes any local changes to Git | ✅ Generic — works with any repo |
| `build_deploy.sh` | rsyncs `output/` to your server, then runs `git_sync.sh` | ⚠️ Fill in your server details before first run |

See the editor's top-bar **Deploy** button for how `build_deploy.sh` gets invoked, and **Git Sync** for `git_sync.sh`.