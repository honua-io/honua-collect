import concurrent.futures,json,os,pathlib,subprocess,time
def aws(*args):
    return json.loads(subprocess.check_output(["aws","devicefarm",*args,"--region","us-west-2","--output","json"]))
project=pathlib.Path(os.environ["HONUA_DEVICEFARM_PROJECT_FILE"]).read_text().strip()
def put(entry):
    name,kind=entry
    u=aws("create-upload","--project-arn",project,"--name",name,"--type",kind)["upload"]
    status = subprocess.run(["curl","-fsS","--max-time","900","--upload-file",name,u["url"]], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if status.returncode != 0:
        raise RuntimeError(f"{kind} transfer failed (curl exit {status.returncode})")
    for _ in range(200):
        status=aws("get-upload","--arn",u["arn"])["upload"]
        if status["status"]=="SUCCEEDED": return u["arn"]
        if status["status"]=="FAILED": raise RuntimeError((kind,status.get("message")))
        time.sleep(3)
    raise TimeoutError(kind)
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as ex:
    a,z,s=ex.map(put,[("field-live.ipa","IOS_APP"),("field-ios-honua-live.zip","APPIUM_PYTHON_TEST_PACKAGE"),("live.yml","APPIUM_PYTHON_TEST_SPEC")])
device=os.environ.get("HONUA_DEVICEFARM_DEVICE_ARN", "arn:aws:devicefarm:us-west-2::device:1ED2EBCF5578549493A578A98C1956B3")
request=dict(projectArn=project,appArn=a,deviceSelectionConfiguration={"filters":[{"attribute":"ARN","operator":"IN","values":[device]}],"maxDevices":1},name="Honua Field iOS installed upgrade and loopback Server replay",test=dict(type="APPIUM_PYTHON",testPackageArn=z,testSpecArn=s),executionConfiguration=dict(skipAppResign=True,jobTimeoutMinutes=45),configuration=dict(billingMethod="METERED"))
pathlib.Path("request.json").write_text(json.dumps(request))
run=aws("schedule-run","--cli-input-json","file://request.json")["run"]["arn"]
pathlib.Path("runarn").write_text(run)
print(run)
