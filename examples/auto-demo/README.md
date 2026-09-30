# Auto build fixture

A dependency-free Node application without a Dockerfile. Publish this directory to a public HTTPS Git repository, create a ForgeDock project with Auto mode, port `8080`, and health path `/`, then deploy. Railpack detects the Node runtime and `npm start` command. The app listens on `0.0.0.0` and reads `PORT`.
