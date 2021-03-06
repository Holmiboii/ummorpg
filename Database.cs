﻿// Saves Character Data in a SQLite database. We use SQLite for serveral reasons
//
// - SQLite is file based and works without having to setup a database server
//   - We can 'remove all ...' or 'modify all ...' easily via SQL queries
//   - A lot of people requested a SQL database and weren't comfortable with XML
//   - We can allow all kinds of character names, even chinese ones without
//     breaking the file system.
// - We will need MYSQL or similar when using multiple server instances later
//   and upgrading is trivial
// - XML is easier, but:
//   - we can't easily read 'just the class of a character' etc., but we need it
//     for character selection etc. often
//   - if each account is a folder that contains players, then we can't save
//     additional account info like password, banned, etc. unless we use an
//     additional account.xml file, which overcomplicates everything
//   - there will always be forbidden file names like 'COM', which will cause
//     problems when people try to create accounts or characters with that name
//
// Tools to open sqlite database files:
//   Windows/OSX program: http://sqlitebrowser.org/
//   Firefox extension: https://addons.mozilla.org/de/firefox/addon/sqlite-manager/
//   Webhost: Adminer/PhpLiteAdmin
//
// About performance:
// - It's recommended to only keep the SQlite connection open while it's used.
//   MMO Servers use it all the time, so we keep it open all the time. This also
//   allows us to use transactions easily, and it will make the transition to
//   MYSQL easier.
// - Transactions are definitely necessary:
//   saving 100 players without transactions takes 3.6s
//   saving 100 players with transactions takes    0.38s
// - Using tr = conn.BeginTransaction() + tr.Commit() and passing it through all
//   the functions is ultra complicated. We use a BEGIN + END queries instead.
//
// Some benchmarks:
//   saving 100 players unoptimized: 4s
//   saving 100 players always open connection + transactions: 3.6s
//   saving 100 players always open connection + transactions + WAL: 3.6s
//   saving 100 players in 1 'using tr = ...' transaction: 380ms
//   saving 100 players in 1 BEGIN/END style transactions: 380ms
//   saving 100 players with XML: 369ms
//
// Build notes:
// - requires Player settings to be set to '.NET' instead of '.NET Subset',
//   otherwise System.Data.dll causes ArgumentException.
// - require x86 and x64 version of sqlite3.dll (found on sqlite website)
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Data;      // copied from Unity/Mono/lib/mono/2.0 to Plugins
using Mono.Data.Sqlite; // copied from Unity/Mono/lib/mono/2.0 to Plugins

public class Database {
    // database path: Application.dataPath is always relative to the project,
    // but we don't want it inside the Assets folder in the Editor (git etc.),
    // instead we put it above that.
    // we also use Path.Combine for platform independent paths
    // and we need persistentDataPath on android
#if UNITY_EDITOR
    static string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Database.sqlite");
#elif UNITY_ANDROID
    static string path = Path.Combine(Application.persistentDataPath, "Database.sqlite");
#elif UNITY_IOS
    static string path = Path.Combine(Application.persistentDataPath, "Database.sqlite");
#else
    static string path = Path.Combine(Application.dataPath, "Database.sqlite");
#endif

    static SqliteConnection conn;

    // constructor /////////////////////////////////////////////////////////////
    static Database() {
        // create database file and setup tables if not exists yet
        if(!File.Exists(path)) {
            // create file
            SqliteConnection.CreateFile(path);

            // open connection        
            conn = new SqliteConnection("URI=file:" + path);
            conn.Open();

            // create tables
            ExecuteNonQuery(@"CREATE TABLE 'characters' (
                                'name' TEXT NOT NULL PRIMARY KEY,
                                'account' TEXT NOT NULL,
                                'class' TEXT NOT NULL,
                                'x' REAL NOT NULL,
                                'y' REAL NOT NULL,
                                'z' REAL NOT NULL,
                                'level' INTEGER NOT NULL,
                                'hp' INTEGER NOT NULL,
                                'mp' INTEGER NOT NULL,
                                'strength' INTEGER NOT NULL,
                                'intelligence' INTEGER NOT NULL,
                                'exp' INTEGER NOT NULL,
                                'skillExp' INTEGER NOT NULL,
                                'gold' INTEGER NOT NULL,
                                'deleted' INTEGER NOT NULL)");

            ExecuteNonQuery(@"CREATE TABLE 'character_inventory' (
                                'character' TEXT NOT NULL,
                                'slot' INTEGER NOT NULL,
                                'name' TEXT NOT NULL,
                                'valid' INTEGER NOT NULL,
                                'amount' INTEGER NOT NULL)");

            ExecuteNonQuery(@"CREATE TABLE 'character_equipment' (
                                'character' TEXT NOT NULL,
                                'slot' INTEGER NOT NULL,
                                'name' TEXT NOT NULL,
                                'valid' INTEGER NOT NULL,
                                'amount' INTEGER NOT NULL)");

            ExecuteNonQuery(@"CREATE TABLE 'character_skills' (
                                'character' TEXT NOT NULL,
                                'name' TEXT NOT NULL,
                                'learned' INTEGER NOT NULL,
                                'level' INTEGER NOT NULL,
                                'castTimeEnd' REAL NOT NULL,
                                'cooldownEnd' REAL NOT NULL,
                                'buffTimeEnd' REAL NOT NULL)");

            ExecuteNonQuery(@"CREATE TABLE 'character_quests' (
                                'character' TEXT NOT NULL,
                                'name' TEXT NOT NULL,
                                'killed' INTEGER NOT NULL,
                                'completed' INTEGER NOT NULL)");

            ExecuteNonQuery(@"CREATE TABLE 'accounts' (
                                'name' TEXT NOT NULL PRIMARY KEY,
                                'password' TEXT NOT NULL,
                                'banned' INTEGER NOT NULL)");

            Debug.Log("created database");
        } else {
            // open connection        
            conn = new SqliteConnection("URI=file:" + path);
            conn.Open();
        }
    }

    // helper functions ////////////////////////////////////////////////////////
    // run a query that doesn't return anything
    public static void ExecuteNonQuery(string sql, params SqliteParameter[] args) {
        using (var cmd = new SqliteCommand(conn)) {
            cmd.CommandText = sql;
            foreach (var param in args) cmd.Parameters.Add(param);
            cmd.ExecuteNonQuery();
        }
    }

    // run a query that returns a single value
    public static object ExecuteScalar(string sql, params SqliteParameter[] args) {
        using (var cmd = new SqliteCommand(conn)) {
            cmd.CommandText = sql;
            foreach (var param in args) cmd.Parameters.Add(param);
            return cmd.ExecuteScalar();
        }
    }

    // run a query that returns several values
    // note: sqlite has long instead of int, so use Convert.ToInt32 etc.
    public static List< List<object> > ExecuteReader(string sql, params SqliteParameter[] args) {
        var result = new List< List<object> >();        

        using (var cmd = new SqliteCommand(conn)) {
            cmd.CommandText = sql;
            foreach (var param in args) cmd.Parameters.Add(param);
            using (var reader = cmd.ExecuteReader()) {
                // the following code causes a SQL EntryPointNotFoundException
                // because sqlite3_column_origin_name isn't found on OSX and
                // some other platforms. newer mono versions have a workaround,
                // but as long as Unity doesn't update, we will have to work
                // around it manually. see also GetSchemaTable function:
                // https://github.com/mono/mono/blob/master/mcs/class/Mono.Data.Sqlite/Mono.Data.Sqlite_2.0/SQLiteDataReader.cs
                //
                //result.Load(reader); (DataTable)
                while (reader.Read()) {
                    var buf = new object[reader.FieldCount];
                    reader.GetValues(buf);
                    result.Add(buf.ToList());
                }
            }
        }

        return result;
    }

    // account data ////////////////////////////////////////////////////////////
    public static bool IsValidAccount(string account, string password) {
        // this function can be used to verify account credentials in a database
        // or a content management system. 
        //        
        // for example, we could setup a content management system with a forum,
        // news, shop etc. and then use a simple HTTP-GET to check the account
        // info, for example:
        //
        //   var request = new WWW("example.com/verify.php?id="+id+"&amp;pw="+pw);
        //   while (!request.isDone)
        //       print("loading...");
        //   return request.error == null && request.text == "ok";
        //
        // where verify.php is a script like this one:
        //   <?php
        //   // id and pw set with HTTP-GET?
        //   if (isset($_GET['id']) && isset($_GET['pw'])) {
        //       // validate id and pw by using the CMS, for example in Drupal:
        //       if (user_authenticate($_GET['id'], $_GET['pw']))
        //           echo "ok";
        //       else
        //           echo "invalid id or pw";
        //   }
        //   ?>
        //
        // or we could check in a MYSQL database:
        //   var dbConn = new MySql.Data.MySqlClient.MySqlConnection("Persist Security Info=False;server=localhost;database=notas;uid=root;password=" + dbpwd);
        //   var cmd = dbConn.CreateCommand();
        //   cmd.CommandText = "SELECT id FROM accounts WHERE id='" + account + "' AND pw='" + password + "'";
        //   dbConn.Open();
        //   var reader = cmd.ExecuteReader();
        //   if (reader.Read())
        //       return reader.ToString() == account;
        //   return false;
        //
        // as usual, we will use the simplest solution possible:
        // create account if not exists, compare password otherwise.
        // no CMS communication necessary and good enough for an Indie MMORPG.

        // not empty?
        if (!Utils.IsNullOrWhiteSpace(account) && !Utils.IsNullOrWhiteSpace(password)) {
            var table = ExecuteReader("SELECT password, banned FROM accounts WHERE name=@name", new SqliteParameter("@name", account));
            if (table.Count == 1) {
                // account exists. check password and ban status.
                var row = table[0];
                return (string)row[0] == password && (long)row[1] == 0;
            } else {
                // account doesn't exist. create it.
                ExecuteNonQuery("INSERT INTO accounts VALUES (@name, @password, 0)", new SqliteParameter("@name", account), new SqliteParameter("@password", password));
                return true;
            }
        }
        return false;
    }

    // character data //////////////////////////////////////////////////////////
    public static bool CharacterExists(string charName) {
        // checks deleted ones too so we don't end up with duplicates if we un-
        // delete one
        return ((long)ExecuteScalar("SELECT Count(*) FROM characters WHERE name=@name", new SqliteParameter("@name", charName))) == 1;
    }

    public static void CharacterDelete(string charName) {
        // soft delete the character so it can always be restored later
        ExecuteNonQuery("UPDATE characters SET deleted=1 WHERE name=@character", new SqliteParameter("@character", charName));
    }

    // returns a dict of<character name, character class=prefab name>
    // we really need the prefab name too, so that client character selection
    // can read all kinds of properties like icons, stats, 3D models and not
    // just the character name
    public static Dictionary<string, string> CharactersForAccount(string account) {
        var result = new Dictionary<string, string>();

        var table = ExecuteReader("SELECT name, class from characters WHERE account=@account AND deleted=0", new SqliteParameter("@account", account));
        foreach (var row in table)
            result[(string)row[0]] = (string)row[1];

        return result;
    }

    public static GameObject CharacterLoad(string charName, List<Player> prefabs) {
        var table = ExecuteReader("SELECT * FROM characters WHERE name=@name AND deleted=0", new SqliteParameter("@name", charName));
        if (table.Count == 1) {
            var mainrow = table[0];

            // instantiate based on the class name
            string className = (string)mainrow[2];
            var prefab = prefabs.Find(p => p.name == className);
            if (prefab != null) {
                var go = (GameObject)GameObject.Instantiate(prefab.gameObject);
                var player = go.GetComponent<Player>();

                player.name               = (string)mainrow[0];
                player.account            = (string)mainrow[1];
                player.className          = (string)mainrow[2];
                var x                     = (float)mainrow[3];
                var y                     = (float)mainrow[4];
                var z                     = (float)mainrow[5];
                // NEVER use player.transform.position = ...; because it
                // places the player at weird positions. for example,
                // (200, 0, -200) becomes (76, 0, -76)
                // using agent.Warp is also recommended in the Unity docs.
                player.agent.Warp(new Vector3(x, y, z));
                player.level              = Convert.ToInt32((long)mainrow[6]);
                player.hp                 = Convert.ToInt32((long)mainrow[7]);
                player.mp                 = Convert.ToInt32((long)mainrow[8]);
                player.strength           = Convert.ToInt32((long)mainrow[9]);
                player.intelligence       = Convert.ToInt32((long)mainrow[10]);
                player.exp                = (long)mainrow[11];
                player.skillExp           = (long)mainrow[12];
                player.gold               = (long)mainrow[13];
                
                // load inventory based on inventorySize (creates slots if none)
                for (int i = 0; i < player.inventorySize; ++i) {
                    // any saved data for that slot?
                    table = ExecuteReader("SELECT name, valid, amount FROM character_inventory WHERE character=@character AND slot=@slot;", new SqliteParameter("@character", player.name), new SqliteParameter("@slot", i));
                    if (table.Count == 1) {
                        var row = table[0];
                        var item = new Item();
                        item.name = (string)row[0];
                        item.valid = ((long)row[1]) != 0; // sqlite has no bool
                        item.amount = Convert.ToInt32((long)row[2]);

                        // add item if template still exists, otherwise empty
                        player.inventory.Add(item.valid && item.TemplateExists() ? item : new Item());
                    } else {
                        // add empty slot or default item if any
                        player.inventory.Add(i < player.defaultItems.Length ? new Item(player.defaultItems[i]) : new Item());
                    }
                }

                // load equipment based on equipmentTypes (creates slots if none)
                for (int i = 0; i < player.equipmentTypes.Length; ++i) {
                    // any saved data for that slot?
                    table = ExecuteReader("SELECT name, valid, amount FROM character_equipment WHERE character=@character AND slot=@slot", new SqliteParameter("@character", player.name), new SqliteParameter("@slot", i));
                    if (table.Count == 1) {
                        var row = table[0];
                        var item = new Item();
                        item.name = (string)row[0];
                        item.valid = ((long)row[1]) != 0; // sqlite has no bool
                        item.amount = Convert.ToInt32((long)row[2]);

                        // add item if template still exists, otherwise empty
                        player.equipment.Add(item.valid && item.TemplateExists() ? item : new Item());
                    } else {
                        // add empty slot or default item if any
                        string equipType = player.equipmentTypes[i];
                        int idx = player.defaultEquipment.FindIndex(equip => player.CanEquip(equipType, new Item(equip)));
                        player.equipment.Add(idx != -1 ? new Item(player.defaultEquipment[idx]) : new Item());
                    }
                }

                // load skills based on skill templates (the others don't matter)
                foreach (var t in player.skillTemplates) {
                    // create skill based on template
                    var skill = new Skill(t);

                    // load saved data if any
                    table = ExecuteReader("SELECT learned, level, castTimeEnd, cooldownEnd, buffTimeEnd FROM character_skills WHERE character=@character AND name=@name", new SqliteParameter("@character", charName), new SqliteParameter("@name", t.name));
                    foreach (var row in table) {
                        skill.learned = ((long)row[0]) != 0; // sqlite has no bool
                        // make sure that 1 <= level <= maxlevel (in case we removed a skill
                        // level etc)
                        skill.level = Mathf.Clamp(Convert.ToInt32((long)row[1]), 1, skill.maxLevel);
                        // castTimeEnd and cooldownEnd are based on Time.time, which
                        // will be different when restarting a server, hence why we
                        // saved them as just the remaining times. so let's convert them
                        // back again.
                        skill.castTimeEnd = (float)row[2] + Time.time;
                        skill.cooldownEnd = (float)row[3] + Time.time;
                        skill.buffTimeEnd = (float)row[4] + Time.time;
                    }

                    player.skills.Add(skill);
                }
                
                // load quests
                table = ExecuteReader("SELECT name, killed, completed FROM character_quests WHERE character=@character", new SqliteParameter("@character", player.name));
                foreach (var row in table) {
                    var quest = new Quest();
                    quest.name = (string)row[0];
                    quest.killed = Convert.ToInt32((long)row[1]);
                    quest.completed = ((long)row[2]) != 0; // sqlite has no bool
                    player.quests.Add(quest.TemplateExists() ? quest : new Quest());
                }

                return go;
            } else Debug.LogError("no prefab found for class: " + className);
        }
        return null;
    }

    // adds or overwrites character data in the database
    public static void CharacterSave(string name, string account, string className, Vector3 position, int level, int hp, int mp, int strength, int intelligence, long exp, long skillExp, long gold, List<Item> inventory, List<Item> equipment, List<Skill> skills, List<Quest> quests) {
        // note: no transaction because savemany already uses one. we could pass
        // a bool inTransactionAlready but it's just overly complicated.
        ExecuteNonQuery("INSERT OR REPLACE INTO characters VALUES (@name, @account, @class, @x, @y, @z, @level, @hp, @mp, @strength, @intelligence, @exp, @skillExp, @gold, 0)",
                        new SqliteParameter("@name", name),
                        new SqliteParameter("@account", account),
                        new SqliteParameter("@class", className),
                        new SqliteParameter("@x", position.x),
                        new SqliteParameter("@y", position.y),
                        new SqliteParameter("@z", position.z),
                        new SqliteParameter("@level", level),
                        new SqliteParameter("@hp", hp),
                        new SqliteParameter("@mp", mp),
                        new SqliteParameter("@strength", strength),
                        new SqliteParameter("@intelligence", intelligence),
                        new SqliteParameter("@exp", exp),
                        new SqliteParameter("@skillExp", skillExp),
                        new SqliteParameter("@gold", gold));

        // inventory: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQuery("DELETE FROM character_inventory WHERE character=@character", new SqliteParameter("@character", name));
        for (int i = 0; i < inventory.Count; ++i) {
            var item = inventory[i];
            ExecuteNonQuery("INSERT INTO character_inventory VALUES (@character, @slot, @name, @valid, @amount)",
                            new SqliteParameter("@character", name),
                            new SqliteParameter("@slot", i),
                            new SqliteParameter("@name", item.valid ? item.name : ""),
                            new SqliteParameter("@valid", Convert.ToInt32(item.valid)),
                            new SqliteParameter("@amount", item.valid ? item.amount : 0));
        }

        // equipment: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQuery("DELETE FROM character_equipment WHERE character=@character", new SqliteParameter("@character", name));
        for (int i = 0; i < equipment.Count; ++i) {
            var item = equipment[i];
            ExecuteNonQuery("INSERT INTO character_equipment VALUES (@character, @slot, @name, @valid, @amount)",
                            new SqliteParameter("@character", name),
                            new SqliteParameter("@slot", i),
                            new SqliteParameter("@name", item.valid ? item.name : ""),
                            new SqliteParameter("@valid", Convert.ToInt32(item.valid)),
                            new SqliteParameter("@amount", item.valid ? item.amount : 0));
        }
        
        // skills: remove old entries first, then add all new ones
        ExecuteNonQuery("DELETE FROM character_skills WHERE character=@character", new SqliteParameter("@character", name));
        foreach (var skill in skills)
            if (skill.learned)
                // castTimeEnd and cooldownEnd are based on Time.time, which
                // will be different when restarting the server, so let's
                // convert them to the remaining time for easier save & load
                // note: this does NOT work when trying to save character data shortly
                //       before closing the editor or game because Time.time is 0 then.
                ExecuteNonQuery("INSERT INTO character_skills VALUES (@character, @name, @learned, @level, @castTimeEnd, @cooldownEnd, @buffTimeEnd)",
                                new SqliteParameter("@character", name),
                                new SqliteParameter("@name", skill.name),
                                new SqliteParameter("@learned", Convert.ToInt32(skill.learned)),
                                new SqliteParameter("@level", skill.level),
                                new SqliteParameter("@castTimeEnd", skill.CastTimeRemaining()),
                                new SqliteParameter("@cooldownEnd", skill.CooldownRemaining()),
                                new SqliteParameter("@buffTimeEnd", skill.BuffTimeRemaining()));

        // quests: remove old entries first, then add all new ones
        ExecuteNonQuery("DELETE FROM character_quests WHERE character=@character", new SqliteParameter("@character", name));
        foreach (var quest in quests)
            ExecuteNonQuery("INSERT INTO character_quests VALUES (@character, @name, @killed, @completed)",
                            new SqliteParameter("@character", name),
                            new SqliteParameter("@name", quest.name),
                            new SqliteParameter("@killed", quest.killed),
                            new SqliteParameter("@completed", Convert.ToInt32(quest.completed)));
    }

    public static void CharacterSave(Player player) {
        CharacterSave(player.name, player.account, player.className, player.transform.position, player.level, player.hp, player.mp, player.strength, player.intelligence, player.exp, player.skillExp, player.gold, player.inventory.ToList(), player.equipment.ToList(), player.skills.ToList(), player.quests.ToList());
    }

    // save multiple characters at once (useful for ultra fast transactions)
    public static void CharacterSaveMany(List<Player> players) {
        ExecuteNonQuery("BEGIN"); // transaction for performance
        foreach (var player in players) Database.CharacterSaveMany(players);
        ExecuteNonQuery("END");
    }
}