# Project templates

Click **New project → Project template**. Choose Node.js, Next.js, React/Vite, FastAPI, Go, ASP.NET Core, or a Node/Redis Compose stack. The picker fills build method, build/start commands, port, health endpoint, and Compose fields. All defaults remain editable; switching templates preserves the project name, repository URL, and branch.

For an existing repository, check that its entry module and health endpoint match the selected defaults. Templates do not change repository contents. For a new app, click **Download starter ZIP**, extract it, and push those files into your own GitHub repository:

```bash
# Run inside the extracted starter directory.
git init -b main
git add .
git commit -m "Start application"
git remote add origin https://github.com/YOU/YOUR-REPOSITORY.git
git push -u origin main
```

For Node, Next.js, and React/Vite, run `npm install` and commit the generated `package-lock.json` before deployment. Vite's provided Dockerfile uses `npm ci` and serves the built assets with nginx on port 8080. Next.js uses `npm run build`, then starts on `0.0.0.0:$PORT`. FastAPI expects `main:app`. Go and ASP.NET Core use automatic runtime detection. The starter health path is `/health`.

The optional **Managed database** selection creates PostgreSQL, Redis, MySQL, SQL Server Express, or MongoDB atomically with the project and saves its encrypted connection variable. Wait for the database to show Running, then deploy. Starter apps do not perform application database migrations or database health checks; add the client, schema, and migrations your app needs. The Compose starter already includes private Redis with a persistent named volume, so an additional managed Redis is usually unnecessary.

Template source is maintained in `examples/templates/` and embedded into the backend for authenticated ZIP downloads. Archives contain no credentials. Dependency ranges are starting defaults; install and commit lockfiles, and keep dependencies updated. Creating a project does not create a GitHub repository, push code, or deploy automatically.

Sources: [Railpack Node detection](https://railpack.com/languages/node), [FastAPI server setup](https://fastapi.tiangolo.com/deployment/manually/), [Next.js CLI](https://nextjs.org/docs/app/api-reference/cli/next).
