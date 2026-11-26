INSERT INTO honua.services(service_name,description,srid,capabilities,metadata)
VALUES ('field_inspections','Field inspections',4326,ARRAY['Query','Create'],
    '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}')
ON CONFLICT DO NOTHING;
INSERT INTO honua.layers(layer_id,layer_name,table_name,geometry_type,srid,extent,metadata)
VALUES (68950,'Field Inspections','features','Point',4326,ST_MakeEnvelope(-180,-90,180,90,4326),
    '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true},"annotations":{"honua.io/attachments":"true"}}')
ON CONFLICT DO NOTHING;
INSERT INTO honua.service_layers(service_name,layer_id,layer_order)
VALUES ('field_inspections',68950,0) ON CONFLICT DO NOTHING;
INSERT INTO honua.layer_fields(layer_id,field_name,field_type,field_order,max_length,nullable)
VALUES
    (68950,'objectid','Integer',0,NULL,false),
    (68950,'name','String',1,128,false),
    (68950,'status','String',2,256,true),
    (68950,'total','Integer',3,NULL,false),
    (68950,'visits','String',4,8192,true),
    (68950,'shape','Geometry',5,NULL,true)
ON CONFLICT DO NOTHING;
