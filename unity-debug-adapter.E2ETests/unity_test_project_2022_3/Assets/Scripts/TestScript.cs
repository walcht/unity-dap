// DO NOT EDIT THIS - ESPECIALLY THE COMMENTS
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TestScript : MonoBehaviour
{
  private readonly float m_Radius = 5.0f;
  static bool s_StaticBoolVar = false;

  // Start is called before the first frame update
  void Start()
  {
    s_StaticBoolVar = true;  // <WATCH>
  }

  // Update is called once per frame
  void Update()
  {
    if (s_StaticBoolVar)
    {
      Debug.Log("1ST BREAKPOINT HERE");  // <BREAKPOINT>
      Debug.Log("-------------------");  // <CONTINUE>
      Debug.Log("-------------------");
      Debug.Log("-------------------");
      Debug.Log("2ND BREAKPOINT HERE");  // <BREAKPOINT>
      s_StaticBoolVar = false;
    }
    Debug.Log("3ND BREAKPOINT HERE");  // <BREAKPOINT>
    ToBeSteppedIntoAndOutOf();         // <STEPINTO>
    Debug.Log("4ND BREAKPOINT HERE");  // <BREAKPOINT>
    Debug.Log("TERMINATE");  // <TERMINATE>
  }

  private string ToBeSteppedIntoAndOutOf()
  {
    string s = "useless var";
    // move to a random position
    transform.position = Random.insideUnitSphere * m_Radius;  // <BREAKPOINT>
    DeepCall1();
    return s;
  }

  private void DeepCall1()
  {
    Debug.Log("DeepCall1");  // <BREAKPOINT>
    DeepCall2();
  }

  private void DeepCall2()
  {
    Debug.Log("DeepCall2");  // <BREAKPOINT>
    DeepCall3();
  }

  private void DeepCall3()
  {
    Debug.Log("DeepCall3");  // <BREAKPOINT>
    DeepCall4();
  }

  private void DeepCall4()
  {
    Debug.Log("DeepCall4");  // <BREAKPOINT>
    DeepCall5();
  }

  private void DeepCall5() => Debug.Log("DeepCall5");
}
