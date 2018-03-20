﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using DarkRift;
using DarkRift.Client;
using DarkRift.Dispatching;
using Spawner.Properties;
using SpawnerLib;
using SpawnerLib.Packets;
using Utils;
using Utils.Messages.Notifications;
using Utils.Messages.Requests;
using Utils.Messages.Responses;

namespace Spawner
{
    public class Program
    {
        static void Main(string[] args)
        {
            var client = new SpawnerClient();
            Console.WriteLine("1: Request Room");
            Console.WriteLine("X: Exit");
            var input = "";
            while (input != null && input.ToLower() != "x")
            {
                input = Console.ReadLine();
                if (input != null)
                {
                    if (input.Equals("1"))
                    {
                        client.SpawnRoom();
                    }
                }
            }
        }
    }

    class SpawnerClient
    {
        private static readonly object ProcessLock = new object();
        private static readonly Dictionary<int, Process> Processes = new Dictionary<int, Process>();

        private int _spawnerId;
        private readonly Queue<int> _freePorts;
        private int _lastPortTaken = -1;
        private DarkRiftClient _client;

        public IPAddress MasterIpAddress { get; set; }
        public int MasterPort { get; set; }

        public string SpawnerIpAddress { get; set; }
        public int SpawnerStartPort { get; set; }
        public int MaxProcesses { get; set; }
        public string ExecutablePath { get; set; }
        public string Region { get; set; }

        public bool AutoStartSpawner { get; set; }

        public bool UseShellExecute { get; set; }
        public bool CreateRoomWindow { get; set; }
        public string ConfigPath { get; set; }

        public SpawnerClient()
        {
            _freePorts = new Queue<int>();

            MasterIpAddress = IPAddress.Parse(Settings.Default.MasterIpAddress);
            MasterPort = Convert.ToInt32(Settings.Default.MasterPort);

            SpawnerIpAddress = Settings.Default.SpawnerIpAddress;
            SpawnerStartPort = Settings.Default.SpawnerStartPort;
            MaxProcesses = Settings.Default.MaxProcesses;
            ExecutablePath = Settings.Default.ExecutablePath;
            Region = Settings.Default.Region;
            AutoStartSpawner = Settings.Default.AutoStartSpawner;
            CreateRoomWindow = Settings.Default.CreateRoomWindow;
            ConfigPath = Settings.Default.ConfigPath;

            _client = new DarkRiftClient();
            _client.ConnectInBackground(MasterIpAddress, MasterPort, IPVersion.IPv4, OnConnectedToMaster);
        }

        //For testing-purposes
        public void SpawnRoom()
        {
            _client.SendMessage(Message.Create(MessageTags.RequestSpawnFromClientToMaster,
                    new RequestSpawnFromClientToMasterMessage {Region = "EU"}), SendMode.Reliable);
        }


        private void OnConnectedToMaster(Exception exception)
        {
            if (exception != null)
            {
                return;
            }

            _client.MessageReceived += OnMessageFromMaster;

            if (AutoStartSpawner)
            {
                _client.SendMessage(Message.Create(MessageTags.RegisterSpawner, new SpawnerOptions
                {
                    Region = Region,
                    MachineIp = SpawnerIpAddress,
                    MaxProcesses = MaxProcesses
                }), SendMode.Reliable);
            }
        }

        private void OnMessageFromMaster(object sender, MessageReceivedEventArgs e)
        {
            using (var message = e.GetMessage())
            {
                switch (message.Tag)
                {
                    case MessageTags.RegisterSpawnerSuccess:
                        HandleRegisterSpawnerSuccess(message);
                        break;
                    case MessageTags.RegisterSpawnerFailed:
                        HandleRegisterSpawnerFailed(message);
                        break;
                    case MessageTags.RequestSpawnFromMasterToSpawner:
                        HandleRequestSpawnFromMaster(message);
                        break;
                    case MessageTags.RequestSpawnFromClientToMasterSuccess:
                        Console.WriteLine("Room will be spawned!");
                        break;
                    case MessageTags.RequestSpawnFromClientToMasterFailed:
                        Console.WriteLine("Spawning room failed: " + message.Deserialize<RequestFailedMessage>().Reason);
                        break;
                }
            }
        }

        private void HandleRequestSpawnFromMaster(Message message)
        {
            var data = message.Deserialize<SpawnRequestPacket>();
            if (data != null)
            {
                var port = GetAvailablePort();
                var startProcessInfo = new ProcessStartInfo(ExecutablePath)
                {
                    CreateNoWindow = !CreateRoomWindow,
                    UseShellExecute = UseShellExecute,
                    Arguments = $"\"{ConfigPath}\" " +
                                $"{ArgNames.MasterIp}={MasterIpAddress} " +
                                $"{ArgNames.MasterPort}={MasterPort} " +
                                $"{ArgNames.SpawnId}={data.SpawnTaskID} " +
                                $"{ArgNames.AssignedPort}={port} " +
                                $"{ArgNames.MachineIp}={SpawnerIpAddress} " +
                                $"{ArgNames.SpawnCode}=\"{data.SpawnCode}\""
                };

                var processStarted = false;
                try
                {
                    new Thread(() =>
                    {
                        try
                        {
                            using (var process = Process.Start(startProcessInfo))
                            {
                                processStarted = true;

                                lock (ProcessLock)
                                {
                                    // Save the process
                                    Processes[data.SpawnTaskID] = process;
                                }

                                var processId = process.Id;

                                // Notify server that we've successfully handled the request (on main thread)
                                new Action(delegate
                                {
                                    _client.SendMessage(Message.Create(MessageTags.RequestSpawnFromMasterToSpawnerSuccess,
                                        new RequestSpawnFromMasterToSpawnerSuccessMessage { SpawnTaskID = data.SpawnTaskID, ProcessID = processId, Arguments = startProcessInfo.Arguments, Status = ResponseStatus.Success }), SendMode.Reliable);
                                }).Invoke();

                                process.WaitForExit();
                            }
                        }
                        catch (Exception e)
                        {

                            if (!processStarted)
                            {
                                new Action(delegate
                                {
                                    _client.SendMessage(Message.Create(MessageTags.RequestSpawnFromMasterToSpawnerFailed,
                                        new RequestSpawnFromMasterToSpawnerFailedMessage { SpawnTaskID = data.SpawnTaskID, Reason = e.ToString(), Status = ResponseStatus.Failed }), SendMode.Reliable);
                                }).Invoke();
                            }
                        }
                        finally
                        {
                            lock (ProcessLock)
                            {
                                // Remove the process
                                Processes.Remove(data.SpawnTaskID);
                            }
                            new Action(delegate
                            {
                                // Release the port number
                                _freePorts.Enqueue(port);
                                _client.SendMessage(Message.Create(MessageTags.NotifySpawnerKilledProcess,
                                    new SpawnerKilledProcessNotificationMessage { SpawnTaskID = data.SpawnTaskID, SpawnerID = _spawnerId }), SendMode.Reliable);
                            }).Invoke();
                        }

                    }).Start();
                }
                catch (Exception e)
                {
                    Console.WriteLine("ThreadException " + data.SpawnTaskID, LogType.Fatal, e);
                }
            }
        }

        private void HandleRegisterSpawnerSuccess(Message message)
        {
            var data = message.Deserialize<RegisterSpawnerSuccessMessage>();
            if (data != null)
            {
                _spawnerId = data.SpawnerID;
                Console.WriteLine("Spawner " + Region + "-" + _spawnerId + " registered to master: " + MasterIpAddress + ":" + MasterPort, LogType.Info);
            }
        }

        private void HandleRegisterSpawnerFailed(Message message)
        {
            Console.WriteLine("Failed to register spawner", LogType.Error);
        }

        private int GetAvailablePort()
        {
            // Return a port from a list of available ports
            if (_freePorts.Count > 0)
                return _freePorts.Dequeue();

            if (_lastPortTaken < 0)
                _lastPortTaken = SpawnerStartPort;

            return _lastPortTaken++;
        }
    }
}