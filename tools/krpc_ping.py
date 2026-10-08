import krpc
c=krpc.connect(name="Grok Bot")
print("connected, scene:", c.krpc.current_game_scene, "| ut:", round(c.space_center.ut,1))
