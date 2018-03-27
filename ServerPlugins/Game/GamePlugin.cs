﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DarkRift;
using DarkRift.Server;
using ServerPlugins.Game.Components;
using ServerPlugins.Game.Entities;
using ServerPlugins.Game.Levels;
using ServerPlugins.Room;
using ServerPlugins.SharpNav.Crowds;
using ServerPlugins.SharpNav.Geometry;
using Utils;
using Utils.Game;

namespace ServerPlugins.Game
{
    /// <summary>
    ///     This Plugin goes to the spawned server
    /// </summary>
    public class GamePlugin : ServerPluginBase
    {
        //Reserve EntityID 0 for null, (example: no target selected)
        private uint _nextEntityID = 1;

        public readonly Dictionary<uint, Entity> Entities;
        private readonly ConcurrentQueue<Entity> _spawnQueue;
        private readonly ConcurrentQueue<Entity> _despawnQueue;

        public override bool ThreadSafe => false;
        public bool Running { get; set; }
        public int Tickrate { get; set; }

        public event Action Started;

        private long _frameCounter = 0;

        private ObjModel level;
        public Crowd Crowd;
        public SharpNav.PolyMesh PolyMesh;
        public SharpNav.PolyMeshDetail PolyMeshDetail;
        public SharpNav.NavMeshQuery NavMeshQuery;



        public GamePlugin(PluginLoadData pluginLoadData) : base(pluginLoadData)
        {
            Tickrate = Convert.ToInt32(pluginLoadData.Settings.Get(nameof(Tickrate)));

            Entities = new Dictionary<uint, Entity>();
            _spawnQueue = new ConcurrentQueue<Entity>();
            _despawnQueue = new ConcurrentQueue<Entity>();
        }


        public void LoadLevel(string levelName)
        {
            level = new ObjModel("Levels/" + levelName + ".obj");

            var settings = SharpNav.NavMeshGenerationSettings.Default;
            settings.AgentHeight = 2;
            settings.AgentRadius = .5f;
            settings.MaxClimb = 0.5f;
            settings.CellSize = .2f;
            var navMesh = SharpNav.NavMesh.Generate(level.GetTriangles(), settings, out PolyMesh, out PolyMeshDetail);

            SharpNav.TiledNavMesh tiledMesh = navMesh;
            NavMeshQuery = new SharpNav.NavMeshQuery(navMesh, 2048);
            Crowd = new Crowd(300, settings.AgentRadius, ref tiledMesh);

            AddEntity(new Monster { Name = "Monster", Position = new Vector3(1f, 0f, 1f)});
        }

        public void AddEntity(Entity entity)
        {
            entity.ID = _nextEntityID++;
            entity.AddComponent<SpawnComponent>();
            entity.AddComponent<NavigationComponent>();
            _spawnQueue.Enqueue(entity);
        }

        public void RemoveEntity(Entity entity)
        {
            _despawnQueue.Enqueue(entity);
        }
        public void Start()
        {
            Running = true;

            Started?.Invoke();

            int updateTime = (int) (1 / (float)Tickrate * 1000);
            while (Running)
            {
                var time = DateTime.Now.Ticks;

                while (_spawnQueue.Count > 0 && _spawnQueue.TryDequeue(out var entity))
                {
                    //TODO: replace foreach-over-all-entites with foreach-entity-in-AREA-OF-INTERESET

                    //let all players observe this unit 
                    foreach (Player player in Entities.Values.Where(e => e is Player))
                    {
                        WriteEvent("Adding " + player.ID + " as observer to " + entity.ID, LogType.Info);
                        entity.Observers.Add(player);
                    }

                    Entities.Add(entity.ID, entity);
                    if (entity is Player newPlayer)
                    {
                        //register the player to all units (around him, including himself), so they send him notifications when something updates
                        foreach (var unit in Entities.Values)
                        {
                            WriteEvent("Adding new Player " + newPlayer.ID + " as observer to " + unit.ID, LogType.Info);
                            unit.Observers.Add(newPlayer);
                        }
                    }
                    
                    entity.Start();
                }

                while (_despawnQueue.Count > 0 && _despawnQueue.TryDequeue(out var entity))
                {
                    Entities.Remove(entity.ID);
                    entity.Destroy();
                }

                int deltaT = (int)(DateTime.Now.Ticks - time);

                UpdateGame();

                if (deltaT < updateTime)
                {
                    Thread.Sleep(updateTime - deltaT);
                }

                ++_frameCounter;
            }
        }

        private void UpdateGame()
        {
            if (_frameCounter % Tickrate == 0) //~1 second elapsed
            {
                WriteEvent("Seconds elapsed (approx.): " + _frameCounter / Tickrate, LogType.Info);
            }

            foreach (var entity in Entities.Values)
            {
                entity.Update();
            }
            foreach (var entity in Entities.Values)
            {
                entity.LateUpdate();
            }  
        }

        public void Stop()
        {
            Running = false;
        }

        public void Log(string message, LogType logType)
        {
            WriteEvent(message, logType);
        }
    }
}