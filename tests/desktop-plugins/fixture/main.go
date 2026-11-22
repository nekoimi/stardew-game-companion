package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"time"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "child" {
		for {
			time.Sleep(time.Second)
		}
	}
	data := os.Getenv("STARDEW_PLUGIN_DATA_DIR")
	_ = os.MkdirAll(data, 0700)
	write := func(name, value string) { _ = os.WriteFile(filepath.Join(data, name), []byte(value), 0600) }
	if len(os.Args) < 3 || os.Args[1] != "--label" || os.Args[2] != "农场 测试" || os.Getenv("OPENAI_API_KEY") != "" {
		write("http-error", fmt.Sprintf("invalid arguments %q or filtered credential", os.Args[1:]))
		os.Exit(2)
	}
	if len(os.Args) == 4 {
		write("configured", "migrated")
		if os.Args[3] == "--fail-migration" {
			os.Exit(4)
		}
	}
	executable, err := os.Executable()
	if err != nil {
		os.Exit(2)
	}
	child := exec.Command(executable, "child")
	if err := child.Start(); err != nil {
		write("http-error", err.Error())
		os.Exit(2)
	}
	write("pid", strconv.Itoa(os.Getpid()))
	write("child-pid", strconv.Itoa(child.Process.Pid))
	token := os.Getenv("STARDEW_PLUGIN_TOKEN")
	host := os.Getenv("STARDEW_PLUGIN_HOST_URL")
	write("address", host)
	// These files belong to the test's isolated temporary installation.
	write("token", token)
	if _, err := os.Stat(filepath.Join(data, "no-hello")); err == nil {
		for {
			time.Sleep(time.Second)
		}
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		os.Exit(2)
	}
	defer listener.Close()
	address := "http://" + listener.Addr().String() + "/settings"
	write("settings", address)
	hello := map[string]any{"protocolMin": 1, "protocolMax": 1, "settingsUrl": address}
	if _, err := os.Stat(filepath.Join(data, "bad-url")); err == nil {
		hello["settingsUrl"] = "https://example.invalid/settings"
	}
	client := &http.Client{Timeout: 2 * time.Second}
	post := func(route string, body map[string]any) map[string]any {
		encoded, _ := json.Marshal(body)
		request, _ := http.NewRequest("POST", host+route, bytes.NewReader(encoded))
		request.Header.Set("Authorization", "Bearer "+token)
		request.Header.Set("Content-Type", "application/json")
		response, err := client.Do(request)
		if err != nil {
			write("http-error", err.Error())
			return nil
		}
		defer response.Body.Close()
		var result map[string]any
		_ = json.NewDecoder(io.LimitReader(response.Body, 65536)).Decode(&result)
		if response.StatusCode >= 400 {
			write("http-error", fmt.Sprintf("%s %v", response.Status, result["message"]))
		}
		return result
	}
	if response := post("/v1/hello", hello); response["instanceId"] == nil {
		os.Exit(3)
	}
	for {
		state := "needs_configuration"
		if _, err := os.Stat(filepath.Join(data, "configured")); err == nil {
			state = "ready"
		}
		_, restart := os.Stat(filepath.Join(data, "restart"))
		response := post("/v1/heartbeat", map[string]any{"state": state, "restartRequired": restart == nil})
		if response["directive"] == "stop" {
			write("stopped", "yes")
			if _, err := os.Stat(filepath.Join(data, "ignore-stop")); err != nil {
				return
			}
		}
		if _, err := os.Stat(filepath.Join(data, "crash")); err == nil {
			_ = os.Remove(filepath.Join(data, "crash"))
			os.Exit(1)
		}
		time.Sleep(250 * time.Millisecond)
	}
}
