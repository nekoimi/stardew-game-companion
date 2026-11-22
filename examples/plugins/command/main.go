package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode/utf16"
)

type command struct {
	RequestID string "json:\"requestId\""
	SessionID string "json:\"sessionId\""
	Text      string "json:\"text\""
}

type sample struct {
	mu        sync.Mutex
	host      string
	token     string
	csrf      string
	directory string
	origin    string
	client    *http.Client
	text      string
	output    string
	last      *command
}

var page = template.Must(template.New("settings").Parse(`<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>指令示例 · 插件设置</title>
<style>
body{background:#f2ebd8;color:#342d24;font:16px "Microsoft YaHei UI",sans-serif;margin:36px auto;padding:0 16px;max-width:680px}
main{background:#fffcf5;border:1px solid #d7bf94;border-radius:10px;padding:22px}
h1{margin:0 0 12px;color:#214231}input{width:100%;box-sizing:border-box;margin:6px 0 12px;padding:10px}
button{background:#214231;color:white;padding:8px 12px;border:0;border-radius:5px;margin:0 6px 6px 0;font:inherit;cursor:pointer}
pre{background:#f6efdc;padding:12px;white-space:pre-wrap;overflow-wrap:anywhere}
</style>
</head>
<body><main>
<h1>指令示例</h1>
<p>保存一条自然语言指令，游戏载入并允许 Agent 操作后手动提交。</p>
<form method="post" action="/settings">
<input type="hidden" name="csrf" value="{{.CSRF}}">
<label>默认指令<input name="text" value="{{.Text}}" maxlength="500"></label>
<button name="intent" value="save">保存指令</button>
<button name="intent" value="submit" {{if not .Text}}disabled{{end}}>提交已保存指令</button>
{{if .HasRequest}}<button name="intent" value="query">查询结果</button><button name="intent" value="retry">重试原请求</button>{{end}}
</form>
{{if .Output}}<pre aria-live="polite">{{.Output}}</pre>{{end}}
</main></body></html>`))

func main() {
	log.SetFlags(log.LstdFlags)
	dir := os.Getenv("STARDEW_PLUGIN_DATA_DIR")
	logDir := os.Getenv("STARDEW_PLUGIN_LOG_DIR")
	if logDir != "" {
		if file, err := os.OpenFile(filepath.Join(logDir, "settings.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600); err == nil {
			defer file.Close()
			log.SetOutput(file)
		}
	}
	host, token := os.Getenv("STARDEW_PLUGIN_HOST_URL"), os.Getenv("STARDEW_PLUGIN_TOKEN")
	if host == "" || token == "" || dir == "" {
		log.Fatal("launch from the plugin host")
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		log.Fatal(err)
	}
	defer listener.Close()
	s := &sample{host: host, token: token, csrf: identifier(), directory: dir, origin: "http://" + listener.Addr().String(),
		client: &http.Client{Timeout: 6 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}}}
	saved, _ := os.ReadFile(filepath.Join(dir, "command.txt"))
	s.text = strings.TrimSpace(string(saved))
	if !validText(s.text) {
		s.text = ""
	}
	if previous, err := os.ReadFile(filepath.Join(dir, "last-command.json")); err == nil {
		var request command
		if json.Unmarshal(previous, &request) == nil && request.RequestID != "" {
			s.last = &request
		}
	}
	var hello struct{ HeartbeatIntervalMs int }
	if err := s.call(http.MethodPost, "/v1/hello", map[string]any{
		"protocolMin": 1, "protocolMax": 1, "settingsUrl": s.origin + "/settings",
	}, &hello); err != nil {
		log.Fatal(err)
	}
	log.Printf("settings: %s/settings", s.origin)
	server := &http.Server{Handler: http.HandlerFunc(s.settings), ReadHeaderTimeout: 5 * time.Second}
	go func() {
		if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Print(err)
		}
	}()
	defer server.Shutdown(context.Background())

	ticker := time.NewTicker(time.Duration(max(hello.HeartbeatIntervalMs, 1000)) * time.Millisecond)
	defer ticker.Stop()
	for {
		s.mu.Lock()
		ready := s.text != ""
		s.mu.Unlock()
		directive, _, err := s.heartbeat(ready)
		if err != nil {
			log.Print(err)
			return
		}
		if directive == "stop" {
			return
		}
		<-ticker.C
	}
}

func (s *sample) settings(w http.ResponseWriter, r *http.Request) {
	if r.Host != strings.TrimPrefix(s.origin, "http://") || r.URL.Path != "/settings" {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'")
	s.mu.Lock()
	defer s.mu.Unlock()
	switch r.Method {
	case http.MethodGet:
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		if err := page.Execute(w, struct {
			CSRF, Text, Output string
			HasRequest         bool
		}{s.csrf, s.text, s.output, s.last != nil}); err != nil {
			log.Print(err)
		}
	case http.MethodPost:
		r.Body = http.MaxBytesReader(w, r.Body, 8192)
		if r.ParseForm() != nil || r.PostForm.Get("csrf") != s.csrf {
			http.Error(w, "设置请求无效", http.StatusForbidden)
			return
		}
		s.output = s.apply(r.PostForm.Get("intent"), r.PostForm.Get("text"))
		http.Redirect(w, r, "/settings", http.StatusSeeOther)
	default:
		w.WriteHeader(http.StatusMethodNotAllowed)
	}
}

func (s *sample) apply(intent, value string) string {
	switch intent {
	case "save":
		text := strings.TrimSpace(value)
		if !validText(text) {
			return "请输入 1 至 500 个字符的指令。"
		}
		if err := writeFile(s.directory, "command.txt", []byte(text)); err != nil {
			return "无法保存指令。"
		}
		s.text = text
		return "指令已保存。"
	case "submit":
		if s.text == "" {
			return "请先保存指令。"
		}
		directive, sessionId, err := s.heartbeat(true)
		if err != nil || directive == "stop" || sessionId == "" {
			return "当前没有可用的游戏会话。"
		}
		request := &command{RequestID: identifier(), SessionID: sessionId, Text: s.text}
		content, _ := json.Marshal(request)
		if writeFile(s.directory, "last-command.json", content) != nil {
			return "无法保存待提交编号。"
		}
		s.last = request
	case "retry", "query":
		if s.last == nil {
			return "先提交指令。"
		}
	default:
		return "无法识别操作。"
	}

	var result json.RawMessage
	var err error
	if intent == "query" {
		err = s.call(http.MethodGet, "/v1/commands/"+s.last.RequestID, nil, &result)
	} else {
		err = s.call(http.MethodPost, "/v1/commands", s.last, &result)
	}
	if err != nil {
		return err.Error()
	}
	var formatted bytes.Buffer
	_ = json.Indent(&formatted, result, "", "  ")
	return formatted.String()
}

func (s *sample) heartbeat(ready bool) (string, string, error) {
	state := "needs_configuration"
	if ready {
		state = "ready"
	}
	var result struct{ Directive, SessionId string }
	err := s.call(http.MethodPost, "/v1/heartbeat", map[string]string{"state": state}, &result)
	return result.Directive, result.SessionId, err
}

func (s *sample) call(method, route string, body any, target any) error {
	var message io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return err
		}
		message = bytes.NewReader(encoded)
	}
	request, err := http.NewRequest(method, s.host+route, message)
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", "Bearer "+s.token)
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	response, err := s.client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	reader := json.NewDecoder(io.LimitReader(response.Body, 1<<20))
	if response.StatusCode >= 400 {
		var problem struct{ Code, Message string }
		if reader.Decode(&problem) == nil && problem.Message != "" {
			return fmt.Errorf("%s (%s)", problem.Message, problem.Code)
		}
		return errors.New(response.Status)
	}
	return reader.Decode(target)
}

func identifier() string {
	var id [16]byte
	if _, err := rand.Read(id[:]); err != nil {
		log.Fatal(err)
	}
	return hex.EncodeToString(id[:])
}

func writeFile(directory, name string, content []byte) error {
	path := filepath.Join(directory, name)
	if err := os.WriteFile(path+".tmp", content, 0600); err != nil {
		return err
	}
	return os.Rename(path+".tmp", path)
}

func validText(value string) bool {
	return value != "" && len(utf16.Encode([]rune(value))) <= 500
}
