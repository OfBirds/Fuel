# Fuel – Manual Test Assets

Use these inputs when running AI estimate tests, UI regression checks, or
whenever you want realistic data to smoke-test the app end-to-end.

---

## Test user credentials

| Field    | Value                   |
|----------|-------------------------|
| Email    | `testuser@fuel.local`   |
| Password | `Test@Fuel2026!`        |
| API base | `http://localhost:5201` |

> The second registered user is `other@fuel.local` / `Other@Fuel2026!` (used for cross-user 403 checks).

---

## AI estimate – text inputs

### 6 meals in plain English

```
250g black bread, 20g butter, 100ml orange juice, and a chocolate muffin
```
```
180g grilled chicken breast, 150g boiled potatoes, a small green salad, and one apple
```
```
200g spaghetti with tomato sauce, 30g grated cheese, and a glass of mineral water
```
```
150g oatmeal with milk, one banana, and a handful of almonds
```
```
120g tuna salad with lettuce and tomatoes, one slice of whole-grain bread, and 200ml lemonade
```
```
300g vegetable soup, 100g rice, and a small piece of dark chocolate
```

### 6 meals with local Serbian names (Latin script)

```
200g šunka, 2 kuvana jaja, tanak parče belog hleba sa kajmakom
```
```
250g pasulj prebranac, jedna kobasica, i kriška domaćeg hleba
```
```
180g ćevapi (5 komada), 100g luk, i lepinja
```
```
200g sarma (2 komada), kašika kiselog kupusa, i 100g pire krompira
```
```
150g gibanica, čaša jogurta (200ml), i jedna paprika
```
```
200g punjene paprike (2 komada), kašika pavlake, i parče ražanog hleba
```

---

## AI estimate – image URLs

BBQ chicken with corn salad:
```
https://food.fnr.sndimg.com/content/dam/images/food/fullset/2013/11/25/0/FNK_sweet-and-spicy-bbq-chicken-with-corn-salad_s4x3.jpg.rend.hgtvcom.791.594.85.suffix/1386297844827.webp
```

Pork and cabbage with wild rice:
```
https://food.fnr.sndimg.com/content/dam/images/food/fullset/2013/11/25/0/FNK_pork-and-cabbage-with-wild-rice_s4x3.jpg.rend.hgtvcom.791.594.85.suffix/1386301477513.webp
```

---

## EAN barcode test

| Field       | Value                                     |
|-------------|-------------------------------------------|
| Barcode     | `20724696`                                |
| Product     | Lidl raw almonds (Бадеми, сурови)         |
| Net weight  | 200 g                                     |
| Origin      | USA                                       |
| Distributor | Lidl Stiftung & Co. KG, Neckarsulm, DE   |
| Best before | 29.12.20xx                                |

> Phase-3 feature (barcode lookup via Open Food Facts) — not yet implemented.
> When wired up, POST the EAN to the barcode endpoint and verify it resolves
> to the correct food definition.

---

## API quick-reference (correct field names)

Endpoints that tripped up raw API tests — use these to avoid silent 400s:

| Resource        | Wrong field used       | Correct field name  |
|-----------------|------------------------|---------------------|
| Food POST/PUT   | `unit`                 | `defaultUoM`        |
| Food ingredient | `foodId`, `amountInBaseUnit` | `childFoodId`, `quantity`, `uoM` |
| Food search     | `q=`                   | `search=`           |
| Entry POST      | `quantityConsumed`, `uoMConsumed`, `caloriesConsumed`, `description` | `quantity`, `uoM`, `calories`, `foodName` |
| Entry batch     | `entries: []`          | `items: []`         |
| Weight POST     | `weightKg`, `date`     | `weight`, `recordedAtUtc` |
| Prefs PUT       | `calorieGoal`          | `dailyCalorieGoal`  |
| AI status       | `/api/user/{id}/estimate/status` | `/api/ai/status` |
| Profile         | `heightCm`, `wristCm`  | `height`, `constitution` (string: Small/Medium/Large) |
| Activity level  | `"Moderately Active"`  | `"moderate"` (frontend lowercase value) |
| Meal pause scope| `"Snacks"`             | `"all"` or `"non-snack"` |

---

## AI provider setup (required before running estimate tests)

1. Copy `deploy/ai-providers.example.json` → `backend/Api/ai-providers.local.json`
2. Enable the provider you want and set `keyRef` (e.g. `"claude"`)
3. Start the backend with:
   ```powershell
   $env:AI_CONFIG_FILE = "ai-providers.local.json"
   $env:AI_KEY_CLAUDE  = "sk-ant-..."   # your Anthropic key
   dotnet run --urls http://localhost:5201
   ```
4. Verify: `GET /api/ai/status` should return `{ "enabled": true, ... }`
