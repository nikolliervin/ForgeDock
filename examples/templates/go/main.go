package main
import ("fmt"; "log"; "net/http"; "os")
func main() {
    port := os.Getenv("PORT"); if port == "" { port = "8080" }
    http.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) { fmt.Fprintln(w, "ok") })
    http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) { fmt.Fprintln(w, "Hello from ForgeDock") })
    log.Fatal(http.ListenAndServe("0.0.0.0:" + port, nil))
}
