using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using TMPro;

public class NetworkUI : MonoBehaviour
{

    void Start()
    {
        Debug.Log("NetworkUI Start called - is editor: " + Application.isEditor);
#if !UNITY_EDITOR
            Debug.Log("Auto-connecting to host...");
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData("192.168.8.126", 7777);
            NetworkManager.Singleton.StartClient();
#endif
    }

    public void StartHost()
    {
        NetworkManager.Singleton.StartHost();
    }

    public void StartClient()
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData("192.168.8.126", 7777);
        NetworkManager.Singleton.StartClient();
    }
}