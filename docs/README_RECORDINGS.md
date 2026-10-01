# README walkthrough recordings

The README includes three looping browser walkthroughs: storage cleanup, release promotion, and documentation search. They capture the actual React interface with synthetic project data and intercepted API responses. Cleanup history and promotion outcomes are simulated, not evidence of a live deployment. No real credentials, applications, images, or storage artifacts are used or modified.

The capture script drives Chromium with Playwright, checks each important state, and captures captioned steps. FFmpeg assembles those screenshots into GIFs at 1080 pixels wide and eight frames per second. Pauses keep the instructions readable; frames between steps are not a continuous screen recording. Intermediate screenshots and conversion manifests stay in the ignored `.runtime/readme-recordings/` directory. Only the optimized GIFs are committed.

## Regenerate

Prerequisites: Node.js, the frontend dependencies, FFmpeg on `PATH`, and Playwright Chromium. No API, worker, Docker containers, or database are needed.

From the repository root:

```bash
npm --prefix web ci
cd web
npx playwright install chromium
cd ..
# In one terminal:
npm --prefix web run dev
# In another terminal:
node web/scripts/record-readme.mjs
```

If Vite is using another port, supply its URL:

```bash
FORGEDOCK_DEMO_URL=http://127.0.0.1:5174 node web/scripts/record-readme.mjs
```

Output:

- `docs/assets/storage-cleanup.gif`
- `docs/assets/release-promotion.gif`
- `docs/assets/documentation-search.gif`

The script uses a fresh browser context for each clip and intercepts all `/api/` requests, including writes. Its only token is a fake documentation token; sign-in is outside the captured steps. Clipboard permissions apply only to these temporary browser contexts. The documentation demo checks that copying an example succeeds. Browser errors fail the recording.

## Verification

On 2026-10-02, all three capture workflows completed with no browser errors. The storage recording checks that unsaved policy changes disable cleanup, then captures confirmation, queued history, and the simulated completed state. The release recording captures the selected staging image and production confirmation. The documentation recording checks clipboard contents after copying an example. The generated GIFs were decoded and checked for multiple frames, dimensions, durations, and successful browser image loading. README image paths use relative links supported by GitHub; adjacent text and linked guides provide static alternatives.
