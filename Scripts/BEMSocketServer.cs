using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Servidor de Sockets TCP Asíncrono para BEM y AI Charging Framework.
/// Permite el control en tiempo real desde agentes externos de Python mediante JSON.
/// Incluye Main-Thread Dispatcher para asegurar llamadas seguras a la API de Unity.
/// </summary>
[DisallowMultipleComponent]
public class BEMSocketServer : MonoBehaviour
{
    [Header("📡 CONFIGURACIÓN DE RED Y SOCKET")]
    [SerializeField] private string serverIP = "127.0.0.1";
    [SerializeField] private int serverPort = 8052;
    [SerializeField] private bool autoStartOnEnable = true;
    [SerializeField] private bool logVerboseTelemetry = true;

    [Header("🔌 NÚCLEO DE FRAMEWORK DE IA")]
    [SerializeField] private AIChargingFramework chargingFramework;

    private TcpListener tcpListener;
    private CancellationTokenSource cancellationTokenSource;
    private bool isRunning = false;

    // Cola de ejecución para despachar solicitudes en el hilo principal de Unity
    private readonly Queue<Action> mainThreadQueue = new Queue<Action>();
    private readonly object queueLock = new object();

    #region PROPIEDADES PÚBLICAS
    public bool IsRunning => isRunning;
    public int ServerPort => serverPort;
    public string ServerIP => serverIP;
    #endregion

    private void Awake()
    {
        EnsureFrameworkReference();
    }

    private void OnEnable()
    {
        if (autoStartOnEnable && !isRunning)
        {
            StartServer();
        }
    }

    private void OnDisable()
    {
        StopServer();
    }

    private void OnApplicationQuit()
    {
        StopServer();
    }

    private void Update()
    {
        // Procesamiento en Hilo Principal de solicitudes acumuladas desde Sockets
        lock (queueLock)
        {
            while (mainThreadQueue.Count > 0)
            {
                try
                {
                    Action action = mainThreadQueue.Dequeue();
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BEM-SOCKET-SERVER] Error en Main-Thread Queue: {ex.Message}");
                }
            }
        }
    }

    private void EnsureFrameworkReference()
    {
        if (chargingFramework == null)
        {
            chargingFramework = FindFirstObjectByType<AIChargingFramework>();
            if (chargingFramework == null)
            {
                chargingFramework = gameObject.AddComponent<AIChargingFramework>();
            }
        }
    }

    // ==========================================
    // CONTROL DEL SERVIDOR TCP
    // ==========================================
    [ContextMenu("🚀 Start BEM Socket Server")]
    public void StartServer()
    {
        if (isRunning)
        {
            Debug.LogWarning("[BEM-SOCKET-SERVER] El servidor ya se encuentra en ejecución.");
            return;
        }

        try
        {
            EnsureFrameworkReference();
            cancellationTokenSource = new CancellationTokenSource();
            IPAddress ipAddress = IPAddress.Parse(serverIP);
            tcpListener = new TcpListener(ipAddress, serverPort);
            tcpListener.Start();
            isRunning = true;

            Debug.Log($"<color=cyan><b>📡 [BEM-SOCKET-SERVER] Escuchando conexiones Python en {serverIP}:{serverPort}</b></color>");

            // Iniciar bucle de escucha asíncrono
            Task.Run(() => AcceptClientsAsync(cancellationTokenSource.Token));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BEM-SOCKET-SERVER] Error al iniciar servidor TCP: {ex.Message}");
            isRunning = false;
        }
    }

    [ContextMenu("🛑 Stop BEM Socket Server")]
    public void StopServer()
    {
        if (!isRunning) return;

        isRunning = false;
        try
        {
            cancellationTokenSource?.Cancel();
            tcpListener?.Stop();
            Debug.Log("<color=yellow><b>🛑 [BEM-SOCKET-SERVER] Servidor TCP detenido correctamente.</b></color>");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BEM-SOCKET-SERVER] Error deteniendo servidor: {ex.Message}");
        }
    }

    // ==========================================
    // BUCLE ASÍNCRONO DE CONEXIONES PYTHON
    // ==========================================
    private async Task AcceptClientsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && isRunning)
        {
            try
            {
                TcpClient client = await tcpListener.AcceptTcpClientAsync();
                if (logVerboseTelemetry)
                {
                    Debug.Log($"<color=lime>[BEM-SOCKET-SERVER] Cliente Python conectado desde: {client.Client.RemoteEndPoint}</color>");
                }

                // Manejar la conexión de cada cliente en una tarea separada
                _ = Task.Run(() => HandleClientSessionAsync(client, token), token);
            }
            catch (ObjectDisposedException)
            {
                // El listener fue detenido voluntariamente
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Debug.LogError($"[BEM-SOCKET-SERVER] Error en aceptación de cliente: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleClientSessionAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    string jsonRequestLine = await reader.ReadLineAsync();

                    if (string.IsNullOrEmpty(jsonRequestLine))
                    {
                        // Cliente se desconectó
                        break;
                    }

                    if (logVerboseTelemetry)
                    {
                        Debug.Log($"<color=cyan><b>📥 [Python ➔ Unity]:</b> {jsonRequestLine}</color>");
                    }

                    // Tarea de completación para esperar la respuesta del Hilo Principal
                    TaskCompletionSource<string> responseTcs = new TaskCompletionSource<string>();

                    // Encolar ejecución en el hilo principal de Unity
                    lock (queueLock)
                    {
                        mainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                string jsonResponse = chargingFramework.ProcessAICommandJson(jsonRequestLine);
                                responseTcs.SetResult(jsonResponse);
                            }
                            catch (Exception ex)
                            {
                                string errJson = $"{{\"success\":false,\"error\":\"{ex.Message}\"}}";
                                responseTcs.SetResult(errJson);
                            }
                        });
                    }

                    // Esperar la respuesta procesada en Unity
                    string replyJson = await responseTcs.Task;

                    // Enviar respuesta serializada de vuelta a Python
                    await writer.WriteLineAsync(replyJson);

                    if (logVerboseTelemetry)
                    {
                        Debug.Log($"<color=lime><b>📤 [Unity ➔ Python]:</b> {replyJson}</color>");
                    }
                }
                catch (IOException)
                {
                    // Conexión interrumpida por el cliente
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BEM-SOCKET-SERVER] Error procesando sesión socket: {ex.Message}");
                    break;
                }
            }
        }

        if (logVerboseTelemetry)
        {
            Debug.Log("<color=yellow>[BEM-SOCKET-SERVER] Sesión con cliente Python finalizada.</color>");
        }
    }
}