
## 13:57 | at/rate-limit-key-updates-2aacfd
Investigated Fuel repo's AI provider configs (ai-providers.json, docker-compose, workflows, env files) and researched Xiaomi token API details (baseUrl, model-id) from vault/homelab docs.
## 14:10 | main
Pushed 3 UI fixes (first-name header, weight width, settings align) from main to release (33cefb8→0867042), triggered prod deploy run 27911828657.
## 14:16 | at/rate-limit-key-updates-2aacfd
Merged rate-limit fallback + DeepSeek-pro config (PR #37→staging), investigated barcode+AI dual failure—barcode has no AI so rate-limit doesn't explain both.
## 14:23 | main
Pushed main→release (3 UI fix commits, 0867042) triggering prod deploy run 27911828657.
## 18:03 | at/rate-limit-key-updates-2aacfd
Merged rate-limit-fallback, added deepseek-pro fallback, created PR #37 for staging (Xiaomi ToS-blocked); found barcode+AI failures from `.remember` auto-push redeploys.